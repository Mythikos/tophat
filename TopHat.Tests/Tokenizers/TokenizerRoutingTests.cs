using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TopHat.DependencyInjection;
using TopHat.Tests.Support;
using TopHat.Tokenizers;
using TopHat.Tokenizers.OpenAI.DependencyInjection;
using TopHat.Transforms;
using Xunit;

namespace TopHat.Tests.Tokenizers;

/// <summary>
/// Verifies the ITokenizer routing rules in TransformPipeline: chars/4 default fires when
/// nothing else is registered; provider-specific tokenizers override the default for their
/// own targets; the tokenizer_kind tag flows through to all related metrics.
/// </summary>
public sealed class TokenizerRoutingTests
{
	private const string PreInstrument = "tophat.request.tokens.pre_transform";
	private const string PostInstrument = "tophat.request.tokens.post_transform";
	private const string RatioInstrument = "tophat.compression.payload.reduction.ratio";

	private static HttpRequestMessage AnthropicPost(string json) =>
		new(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
		{
			Content = new StringContent(json, Encoding.UTF8, "application/json"),
		};

	private static HttpRequestMessage OpenAIPost(string json) =>
		new(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
		{
			Content = new StringContent(json, Encoding.UTF8, "application/json"),
		};

	[Fact]
	public async Task DefaultRegistration_EmitsCharsPerTokenKind()
	{
		// AddTopHat() registers CharsPerTokenTokenizer as the only ITokenizer. All metrics
		// should carry tokenizer_kind=chars_per_token.
		using var capture = new MetricsCapture();

		var (client, _, _) = TransformHandlerFactory.Build(
			s => s.AddTopHatRequestTransform<NoOpRequestTransform>(),
			behavior: (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

		using var req = AnthropicPost("""{"model":"claude-haiku-4-5","stream":false,"messages":[]}""");
		using var _ = await client.SendAsync(req);

		var pre = capture.ForInstrument(PreInstrument).Single();
		var post = capture.ForInstrument(PostInstrument).Single();
		Assert.Equal("chars_per_token", pre.Tag("tokenizer_kind"));
		Assert.Equal("chars_per_token", post.Tag("tokenizer_kind"));
	}

	[Fact]
	public async Task OpenAITokenizerRegistered_WinsForOpenAIChatCompletionsTarget()
	{
		// AddTopHatOpenAITokenizer registers tiktoken alongside the chars/4 fallback. The
		// pipeline picks tiktoken for OpenAIChatCompletions targets — tag should reflect that.
		using var capture = new MetricsCapture();

		var (client, _, _) = TransformHandlerFactory.Build(
			s =>
			{
				s.AddTopHatOpenAITokenizer();
				s.AddTopHatRequestTransform<NoOpRequestTransform>();
			},
			behavior: (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

		using var req = OpenAIPost("""{"model":"gpt-4o","stream":false,"messages":[{"role":"user","content":"hi"}]}""");
		using var _ = await client.SendAsync(req);

		var pre = capture.ForInstrument(PreInstrument).Single();
		Assert.Equal("tiktoken", pre.Tag("tokenizer_kind"));
		Assert.Equal("OpenAIChatCompletions", pre.Tag("target"));
		// Tiktoken should produce a positive integer token count for non-empty bodies.
		Assert.True(pre.Value > 0);
	}

	[Fact]
	public async Task OpenAITokenizerRegistered_FallsBackToCharsPerTokenForAnthropicTarget()
	{
		// OpenAI tokenizer doesn't support AnthropicMessages, so chars/4 takes over for those.
		using var capture = new MetricsCapture();

		var (client, _, _) = TransformHandlerFactory.Build(
			s =>
			{
				s.AddTopHatOpenAITokenizer();
				s.AddTopHatRequestTransform<NoOpRequestTransform>();
			},
			behavior: (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

		using var req = AnthropicPost("""{"model":"claude-haiku-4-5","stream":false,"messages":[]}""");
		using var _ = await client.SendAsync(req);

		var pre = capture.ForInstrument(PreInstrument).Single();
		Assert.Equal("chars_per_token", pre.Tag("tokenizer_kind"));
	}

	[Fact]
	public async Task TokenizerKindTagOnRatioHistogram()
	{
		// Reduction ratio histogram must carry tokenizer_kind too — pre/post are paired and
		// dashboards comparing ratios across tokenizers need the disambiguator.
		using var capture = new MetricsCapture();

		var (client, _, _) = TransformHandlerFactory.Build(
			s =>
			{
				s.AddTopHatOpenAITokenizer();
				s.AddTopHatRequestTransform<ExampleMetadataStripTransform>();
			},
			behavior: (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

		using var req = OpenAIPost("""{"model":"gpt-4o","stream":false,"messages":[],"metadata":{"trace":"abcdefghij"}}""");
		using var _ = await client.SendAsync(req);

		var ratio = capture.ForInstrument(RatioInstrument).Single();
		Assert.Equal("tiktoken", ratio.Tag("tokenizer_kind"));
	}

	[Fact]
	public async Task TiktokenIsAccurate_NotCharsPerFour()
	{
		// Sanity check: tiktoken counts must NOT equal len/4 — that'd mean we accidentally
		// fell back to chars/4 despite registering the OpenAI tokenizer.
		using var capture = new MetricsCapture();

		var (client, _, _) = TransformHandlerFactory.Build(
			s =>
			{
				s.AddTopHatOpenAITokenizer();
				s.AddTopHatRequestTransform<NoOpRequestTransform>();
			},
			behavior: (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

		// Body designed to be very different in tiktoken vs chars/4: lots of common-word
		// content that tokenizes more efficiently than 4 chars/token.
		var body = """{"model":"gpt-4o","stream":false,"messages":[{"role":"user","content":"the quick brown fox jumps over the lazy dog. the quick brown fox jumps over the lazy dog. the quick brown fox jumps over the lazy dog."}]}""";
		using var req = OpenAIPost(body);
		using var _ = await client.SendAsync(req);

		var pre = capture.ForInstrument(PreInstrument).Single();
		var charsOver4 = body.Length / 4;
		Assert.NotEqual(charsOver4, (long)pre.Value);
	}
}
