using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TopHat.Providers;
using TopHat.Tokenizers;

namespace TopHat.Tokenizers.Anthropic;

/// <summary>
/// Bit-exact <see cref="ITokenizer"/> for Anthropic-targeted requests, backed by Anthropic's
/// <c>https://api.anthropic.com/v1/messages/count_tokens</c> endpoint. Counting via the API
/// is free (does not count against TPM and is not billed) but it's a network round-trip per
/// count, which would add latency to the request path if executed inline.
/// </summary>
/// <remarks>
/// <para>
/// Two modes via <see cref="AnthropicTokenizerOptions.Mode"/>:
/// <list type="bullet">
///   <item><description><see cref="AnthropicTokenizerMode.Deferred"/> (default): returns 0
///   synchronously, schedules the count_tokens call in the background, and emits the real
///   count via the pipeline-provided <c>deferredEmit</c> callback when it arrives. Zero
///   request-path latency; reduction-ratio histogram is 0 per request (the pipeline can't
///   know the real values at request time) but pre/post cumulative counters end up correct.</description></item>
///   <item><description><see cref="AnthropicTokenizerMode.Blocking"/>: awaits the HTTP call
///   inline, returns the real count to the pipeline. Adds ~50-100ms latency per request.
///   Pre/post counters AND the reduction-ratio histogram emit accurate per-request values.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed partial class AnthropicCountTokensTokenizer : ITokenizer
{
	/// <summary>
	/// Fields the count_tokens endpoint accepts. Anything else in a /v1/messages-shaped body
	/// (max_tokens, temperature, top_p, top_k, stream, stop_sequences, metadata, etc.) is
	/// rejected with "Extra inputs are not permitted" and must be stripped before forwarding.
	/// Source: Anthropic API docs for /v1/messages/count_tokens.
	/// </summary>
	private static readonly HashSet<string> s_countTokensAllowedFields = new(StringComparer.Ordinal)
	{
		"model",
		"messages",
		"system",
		"tools",
		"tool_choice",
		"thinking",
		"mcp_servers",
	};

	private readonly HttpClient _http;
	private readonly IOptions<AnthropicTokenizerOptions> _options;
	private readonly ILogger<AnthropicCountTokensTokenizer> _logger;

	public AnthropicCountTokensTokenizer(HttpClient http, IOptions<AnthropicTokenizerOptions> options, ILogger<AnthropicCountTokensTokenizer> logger)
	{
		ArgumentNullException.ThrowIfNull(http);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(logger);
		this._http = http;
		this._options = options;
		this._logger = logger;
	}

	/// <inheritdoc/>
	public string Kind => "anthropic_api";

	/// <inheritdoc/>
	public bool SupportsTarget(TopHatTarget target) => target == TopHatTarget.AnthropicMessages;

	/// <inheritdoc/>
	public ValueTask<int> CountTokensAsync(ReadOnlyMemory<byte> body, TopHatTarget target, string? model, Action<int>? deferredEmit, CancellationToken cancellationToken)
	{
		if (body.IsEmpty)
		{
			return new ValueTask<int>(0);
		}

		var opts = this._options.Value;

		if (opts.Mode == AnthropicTokenizerMode.Blocking || deferredEmit is null)
		{
			// Either user explicitly chose blocking, or the caller didn't provide a deferred-emit
			// callback (e.g., direct unit-test usage outside the pipeline). Fall through to await.
			return new ValueTask<int>(this.CountInlineAsync(body, opts, cancellationToken));
		}

		// Deferred: copy the body bytes into a fresh array (caller-owned memory may be reused),
		// kick a background task that calls the API and emits via the callback, return 0 now.
		var bodyCopy = body.ToArray();
		_ = Task.Run(async () =>
		{
			try
			{
				var count = await this.CountInlineAsync(bodyCopy, opts, CancellationToken.None).ConfigureAwait(false);
				if (count > 0)
				{
					deferredEmit(count);
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				LogDeferredFailed(this._logger, ex);
			}
		}, CancellationToken.None);

		return new ValueTask<int>(0);
	}

	private async Task<int> CountInlineAsync(ReadOnlyMemory<byte> body, AnthropicTokenizerOptions opts, CancellationToken cancellationToken)
	{
		var apiKey = opts.ApiKey ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
		if (string.IsNullOrEmpty(apiKey))
		{
			LogMissingApiKey(this._logger);
			return 0;
		}

		// count_tokens accepts a SUBSET of /v1/messages fields: anything outside
		// s_countTokensAllowedFields gets rejected with "Extra inputs are not permitted".
		// Strip disallowed top-level keys before forwarding. If the body isn't parseable JSON,
		// fall through with the original bytes — count_tokens will return 400 and we'll log it.
		var filteredBytes = FilterToCountTokensFields(body);
		using var request = new HttpRequestMessage(HttpMethod.Post, opts.BaseUrl.TrimEnd('/') + "/v1/messages/count_tokens")
		{
			Content = new ByteArrayContent(filteredBytes),
		};
		request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
		request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
		request.Headers.TryAddWithoutValidation("anthropic-version", opts.AnthropicVersion);

		HttpResponseMessage response;
		try
		{
			response = await this._http.SendAsync(request, cancellationToken).ConfigureAwait(false);
		}
		catch (HttpRequestException ex)
		{
			LogRequestFailed(this._logger, ex);
			return 0;
		}

		using (response)
		{
			if (!response.IsSuccessStatusCode)
			{
				// Read the body so the warning includes Anthropic's error message — without
				// it, "400 Bad Request" tells the user nothing actionable.
				var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
				LogNonSuccessStatus(this._logger, (int)response.StatusCode, errorBody);
				return 0;
			}

			var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
			await using (stream.ConfigureAwait(false))
			{
				JsonNode? parsed;
				try
				{
					parsed = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
				}
				catch (System.Text.Json.JsonException ex)
				{
					LogInvalidResponseBody(this._logger, ex);
					return 0;
				}

				if (parsed is not JsonObject obj || obj["input_tokens"] is not JsonValue tokenValue || !tokenValue.TryGetValue<int>(out var count))
				{
					return 0;
				}

				return count;
			}
		}
	}

	/// <summary>
	/// Strips top-level fields not accepted by count_tokens. Returns the filtered body as
	/// UTF-8 bytes. If the input isn't a parseable JSON object, returns the original bytes
	/// unchanged — the API will reject it and we'll log the resulting 400.
	/// </summary>
	private static byte[] FilterToCountTokensFields(ReadOnlyMemory<byte> body)
	{
		JsonNode? parsed;
		try
		{
			parsed = JsonNode.Parse(body.Span);
		}
		catch (System.Text.Json.JsonException)
		{
			return body.ToArray();
		}

		if (parsed is not JsonObject obj)
		{
			return body.ToArray();
		}

		var filtered = new JsonObject();
		foreach (var kvp in obj)
		{
			if (s_countTokensAllowedFields.Contains(kvp.Key))
			{
				// DeepClone so the source object's parent linkage isn't disturbed.
				filtered[kvp.Key] = kvp.Value?.DeepClone();
			}
		}

		return Encoding.UTF8.GetBytes(filtered.ToJsonString());
	}

	[LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Anthropic count_tokens skipped: no API key (set ANTHROPIC_API_KEY or AnthropicTokenizerOptions.ApiKey). Pre/post token metrics will not emit for Anthropic requests.")]
	private static partial void LogMissingApiKey(ILogger logger);

	[LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Anthropic count_tokens HTTP request failed; pre/post token metrics will not emit for this request.")]
	private static partial void LogRequestFailed(ILogger logger, Exception exception);

	[LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Anthropic count_tokens returned non-success status {StatusCode} (response body: {ResponseBody}); pre/post token metrics will not emit for this request.")]
	private static partial void LogNonSuccessStatus(ILogger logger, int statusCode, string responseBody);

	[LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Anthropic count_tokens response body was not valid JSON.")]
	private static partial void LogInvalidResponseBody(ILogger logger, Exception exception);

	[LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Anthropic count_tokens deferred background task failed; metric not emitted for this request.")]
	private static partial void LogDeferredFailed(ILogger logger, Exception exception);
}
