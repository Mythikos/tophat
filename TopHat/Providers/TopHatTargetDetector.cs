using Microsoft.Extensions.Options;
using TopHat.Configuration;

namespace TopHat.Providers;

/// <summary>
/// Classifies requests by provider and endpoint, resolving shared gateway hosts from their API paths.
/// Provider detection drives URI rewriting; target detection drives transforms, CCR, and metrics tags.
/// These are deliberately separate concerns — a rewrite applies to unknown targets on a known host too.
/// </summary>
internal sealed class TopHatTargetDetector
{
	private const string ANTHROPIC_DEFAULT_HOST = "api.anthropic.com";
	private const string OPENAI_DEFAULT_HOST = "api.openai.com";

	private readonly IOptions<TopHatOptions> _options;

	/// <summary>Creates a detector using the configured provider gateway addresses.</summary>
	public TopHatTargetDetector(IOptions<TopHatOptions> options)
	{
		this._options = options;
	}

	/// <summary>Resolves canonical hosts first, then configured hosts and unambiguous shared gateway endpoints.</summary>
	public TopHatProviderKind DetectProvider(HttpRequestMessage request)
	{
		ArgumentNullException.ThrowIfNull(request);
		var host = Normalize(request.RequestUri?.Host);
		if (host is null)
		{
			return TopHatProviderKind.Other;
		}

		if (HostMatches(host, ANTHROPIC_DEFAULT_HOST))
		{
			return TopHatProviderKind.Anthropic;
		}

		if (HostMatches(host, OPENAI_DEFAULT_HOST))
		{
			return TopHatProviderKind.OpenAI;
		}

		var matchesAnthropic = HostMatches(host, Normalize(this._options.Value.AnthropicBaseUrl?.Host));
		var matchesOpenAi = HostMatches(host, Normalize(this._options.Value.OpenAiBaseUrl?.Host));
		if (matchesAnthropic && matchesOpenAi)
		{
			var anthropicEndpoint = this.DetectTarget(request, TopHatProviderKind.Anthropic) != TopHatTarget.Unknown;
			var openAiEndpoint = this.DetectTarget(request, TopHatProviderKind.OpenAI) != TopHatTarget.Unknown;
			return (anthropicEndpoint, openAiEndpoint) switch
			{
				(true, false) => TopHatProviderKind.Anthropic,
				(false, true) => TopHatProviderKind.OpenAI,
				_ => TopHatProviderKind.Other,
			};
		}

		return matchesAnthropic ? TopHatProviderKind.Anthropic : matchesOpenAi ? TopHatProviderKind.OpenAI : TopHatProviderKind.Other;
	}

	/// <summary>Classifies the endpoint using the provider established by the current request URI.</summary>
	public TopHatTarget DetectTarget(HttpRequestMessage request) => this.DetectTarget(request, this.DetectProvider(request));

	/// <summary>Classifies the endpoint without losing a provider identity established before URI rewriting.</summary>
	public TopHatTarget DetectTarget(HttpRequestMessage request, TopHatProviderKind provider)
	{
		ArgumentNullException.ThrowIfNull(request);
		var path = request.RequestUri?.AbsolutePath ?? string.Empty;
		var target = ClassifyPath(path, provider);
		if (target != TopHatTarget.Unknown)
		{
			return target;
		}

		var baseUrl = provider switch
		{
			TopHatProviderKind.Anthropic => this._options.Value.AnthropicBaseUrl,
			TopHatProviderKind.OpenAI => this._options.Value.OpenAiBaseUrl,
			_ => null,
		};
		var host = Normalize(request.RequestUri?.Host);
		if (baseUrl is null || host is null || !HostMatches(host, Normalize(baseUrl.Host)))
		{
			return TopHatTarget.Unknown;
		}

		// A configured mount prefix applies only to that gateway and on a whole path segment.
		// Recognize ordinary /v1 paths first: URI rewriting preserves the caller's original path.
		var prefix = baseUrl.AbsolutePath.TrimEnd('/');
		if (prefix.Length == 0 || path.Length <= prefix.Length || !path.StartsWith(prefix, StringComparison.Ordinal) || path[prefix.Length] != '/')
		{
			return TopHatTarget.Unknown;
		}

		return ClassifyPath(path[prefix.Length..], provider);
	}

	private static TopHatTarget ClassifyPath(string path, TopHatProviderKind provider) => provider switch
	{
		TopHatProviderKind.Anthropic => ClassifyAnthropic(path),
		TopHatProviderKind.OpenAI => ClassifyOpenAI(path),
		_ => TopHatTarget.Unknown,
	};

	private static TopHatTarget ClassifyAnthropic(string path) => path switch
	{
		"/v1/messages" => TopHatTarget.AnthropicMessages,
		"/v1/messages/count_tokens" => TopHatTarget.AnthropicCountTokens,
		_ when IsEndpointOrChild(path, "/v1/messages/batches") => TopHatTarget.AnthropicBatches,
		_ => TopHatTarget.Unknown,
	};

	private static TopHatTarget ClassifyOpenAI(string path) => path switch
	{
		"/v1/chat/completions" => TopHatTarget.OpenAIChatCompletions,
		_ when IsEndpointOrChild(path, "/v1/responses") => TopHatTarget.OpenAIResponses,
		_ when IsEndpointOrChild(path, "/v1/batches") => TopHatTarget.OpenAIBatches,
		_ => TopHatTarget.Unknown,
	};

	private static bool IsEndpointOrChild(string path, string endpoint) => path.StartsWith(endpoint, StringComparison.Ordinal) && (path.Length == endpoint.Length || path[endpoint.Length] == '/');

	private static string? Normalize(string? host)
	{
		if (string.IsNullOrEmpty(host))
		{
			return null;
		}

		return host.EndsWith('.') ? host[..^1] : host;
	}

	private static bool HostMatches(string host, string? candidate)
	{
		return candidate is not null && string.Equals(host, candidate, StringComparison.OrdinalIgnoreCase);
	}
}
