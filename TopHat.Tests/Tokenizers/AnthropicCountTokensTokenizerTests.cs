using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TopHat.Providers;
using TopHat.Tokenizers.Anthropic;
using Xunit;

namespace TopHat.Tests.Tokenizers;

/// <summary>
/// Unit tests for the Anthropic count_tokens tokenizer. Uses a fake HttpMessageHandler so the
/// tests don't hit the real API — verifies request shape going out and response parsing
/// coming back.
/// </summary>
public sealed class AnthropicCountTokensTokenizerTests
{
	[Fact]
	public async Task CountTokensAsync_StripsDisallowedFieldsBeforeSending()
	{
		// count_tokens rejects fields like max_tokens, temperature, etc. with "Extra inputs are
		// not permitted". The tokenizer must filter the body to only count_tokens-accepted
		// fields (model, messages, system, tools, tool_choice, thinking, mcp_servers).
		string? capturedRequestBody = null;
		var fake = new FakeHttpHandler((req, ct) =>
		{
			capturedRequestBody = req.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult();
			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent("""{"input_tokens": 100}""", Encoding.UTF8, "application/json"),
			};
		});

		var tokenizer = Build(fake, "test-key");
		// Body includes max_tokens (disallowed) and temperature (disallowed) alongside model + messages.
		var body = """{"model":"claude-haiku-4-5","max_tokens":1000,"temperature":0.5,"messages":[{"role":"user","content":"hi"}]}"""u8;

		await tokenizer.CountTokensAsync(body.ToArray(), TopHatTarget.AnthropicMessages, "claude-haiku-4-5", deferredEmit: null, CancellationToken.None);

		Assert.NotNull(capturedRequestBody);
		Assert.DoesNotContain("max_tokens", capturedRequestBody, StringComparison.Ordinal);
		Assert.DoesNotContain("temperature", capturedRequestBody, StringComparison.Ordinal);
		Assert.Contains("\"model\"", capturedRequestBody, StringComparison.Ordinal);
		Assert.Contains("\"messages\"", capturedRequestBody, StringComparison.Ordinal);
	}

	[Fact]
	public async Task CountTokensAsync_ParsesInputTokensFromResponse()
	{
		var fake = new FakeHttpHandler((req, _) =>
		{
			Assert.Equal("https://api.anthropic.com/v1/messages/count_tokens", req.RequestUri!.ToString());
			Assert.True(req.Headers.Contains("x-api-key"));
			Assert.True(req.Headers.Contains("anthropic-version"));
			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent("""{"input_tokens": 1234}""", Encoding.UTF8, "application/json"),
			};
		});

		var tokenizer = Build(fake, "test-key");
		var body = """{"model":"claude-haiku-4-5","messages":[{"role":"user","content":"hi"}]}"""u8;

		var count = await tokenizer.CountTokensAsync(body.ToArray(), TopHatTarget.AnthropicMessages, "claude-haiku-4-5", deferredEmit: null, CancellationToken.None);

		Assert.Equal(1234, count);
	}

	[Fact]
	public async Task CountTokensAsync_ReturnsZeroOnNon200Status()
	{
		var fake = new FakeHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest)
		{
			Content = new StringContent("""{"error":"bad request"}""", Encoding.UTF8, "application/json"),
		});

		var tokenizer = Build(fake, "test-key");
		var body = """{"model":"x"}"""u8;

		var count = await tokenizer.CountTokensAsync(body.ToArray(), TopHatTarget.AnthropicMessages, null, deferredEmit: null, CancellationToken.None);

		Assert.Equal(0, count);
	}

	[Fact]
	public async Task CountTokensAsync_ReturnsZeroOnMissingApiKey()
	{
		// When neither options.ApiKey nor ANTHROPIC_API_KEY is set, the tokenizer skips the
		// HTTP call entirely and returns 0. The fake handler should not be invoked.
		var invoked = false;
		var fake = new FakeHttpHandler((_, _) =>
		{
			invoked = true;
			return new HttpResponseMessage(HttpStatusCode.OK);
		});

		// Save and clear the env var for the duration of this test so the fallback doesn't fire.
		var saved = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
		Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
		try
		{
			var tokenizer = Build(fake, apiKey: null);
			var body = """{"model":"x"}"""u8;
			var count = await tokenizer.CountTokensAsync(body.ToArray(), TopHatTarget.AnthropicMessages, null, deferredEmit: null, CancellationToken.None);

			Assert.Equal(0, count);
			Assert.False(invoked);
		}
		finally
		{
			Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", saved);
		}
	}

	[Fact]
	public async Task CountTokensAsync_ReturnsZeroOnEmptyBody()
	{
		// Defensive guard: empty input → 0 without firing an HTTP call.
		var invoked = false;
		var fake = new FakeHttpHandler((_, _) =>
		{
			invoked = true;
			return new HttpResponseMessage(HttpStatusCode.OK);
		});

		var tokenizer = Build(fake, "test-key");

		var count = await tokenizer.CountTokensAsync(ReadOnlyMemory<byte>.Empty, TopHatTarget.AnthropicMessages, null, deferredEmit: null, CancellationToken.None);

		Assert.Equal(0, count);
		Assert.False(invoked);
	}

	[Fact]
	public async Task DeferredMode_ReturnsZeroSync_AndInvokesCallbackAsync()
	{
		// Verifies the deferred contract: synchronous return is 0, real count arrives later via
		// the deferredEmit callback. The pipeline relies on this exact behavior — Add(0) sync +
		// Add(realCount) deferred = correct cumulative.
		var fake = new FakeHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("""{"input_tokens":777}""", Encoding.UTF8, "application/json"),
		});

		var http = new HttpClient(fake);
		var options = Options.Create(new AnthropicTokenizerOptions
		{
			ApiKey = "test-key",
			Mode = AnthropicTokenizerMode.Deferred,
		});
		var tokenizer = new AnthropicCountTokensTokenizer(http, options, NullLogger<AnthropicCountTokensTokenizer>.Instance);

		var emittedCount = 0;
		var callbackFired = new TaskCompletionSource();
		Action<int> callback = c =>
		{
			emittedCount = c;
			callbackFired.TrySetResult();
		};

		var body = """{"model":"claude-haiku-4-5","messages":[{"role":"user","content":"hi"}]}"""u8;
		var syncReturn = await tokenizer.CountTokensAsync(body.ToArray(), TopHatTarget.AnthropicMessages, "claude-haiku-4-5", callback, CancellationToken.None);

		Assert.Equal(0, syncReturn);

		// Wait for the background task to complete and the callback to fire.
		await callbackFired.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(777, emittedCount);
	}

	[Fact]
	public void SupportsTarget_OnlyAnthropicMessages()
	{
		var tokenizer = Build(new FakeHttpHandler((_, _) => new HttpResponseMessage()), "k");
		Assert.True(tokenizer.SupportsTarget(TopHatTarget.AnthropicMessages));
		Assert.False(tokenizer.SupportsTarget(TopHatTarget.OpenAIChatCompletions));
		Assert.False(tokenizer.SupportsTarget(TopHatTarget.OpenAIResponses));
		Assert.False(tokenizer.SupportsTarget(TopHatTarget.AnthropicCountTokens));
	}

	private static AnthropicCountTokensTokenizer Build(FakeHttpHandler handler, string? apiKey)
	{
		var http = new HttpClient(handler);
		var options = Options.Create(new AnthropicTokenizerOptions { ApiKey = apiKey });
		return new AnthropicCountTokensTokenizer(http, options, NullLogger<AnthropicCountTokensTokenizer>.Instance);
	}

	private sealed class FakeHttpHandler : HttpMessageHandler
	{
		private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _behavior;

		public FakeHttpHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> behavior) => this._behavior = behavior;

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(this._behavior(request, cancellationToken));
	}
}
