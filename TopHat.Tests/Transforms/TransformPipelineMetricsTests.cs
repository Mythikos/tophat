using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TopHat.DependencyInjection;
using TopHat.Tests.Support;
using TopHat.Transforms;
using Xunit;

namespace TopHat.Tests.Transforms;

/// <summary>
/// Verifies that the transform pipeline emits the new cost-transparency instruments:
/// pre/post payload token estimates, compression reduction ratio, and Anthropic cache bust
/// detection. Outcome labels and tag shapes here form a dashboard contract.
/// </summary>
public sealed class TransformPipelineMetricsTests
{
	private const string PreInstrument = "tophat.request.tokens.pre_transform";
	private const string PostInstrument = "tophat.request.tokens.post_transform";
	private const string RatioInstrument = "tophat.compression.payload.reduction.ratio";
	private const string BustInstrument = "tophat.cache.busts_detected";

	private static HttpRequestMessage AnthropicPost(string json) =>
		new(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
		{
			Content = new StringContent(json, Encoding.UTF8, "application/json"),
		};

	private static HttpRequestMessage OpenAIChatPost(string json) =>
		new(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
		{
			Content = new StringContent(json, Encoding.UTF8, "application/json"),
		};

	[Fact]
	public async Task EmitsPrePostTokenEstimatesAndRatio_OnMutatingTransform()
	{
		using var capture = new MetricsCapture();

		var (client, _, _) = TransformHandlerFactory.Build(
			s => s.AddTopHatRequestTransform<ExampleMetadataStripTransform>(),
			behavior: (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

		// Body large enough that pre and post differ by enough to register a non-zero ratio.
		using var req = AnthropicPost("""{"model":"claude-haiku-4-5","stream":false,"messages":[],"metadata":{"trace":"abcdefghijklmnop"}}""");
		using var _ = await client.SendAsync(req);

		var pre = capture.ForInstrument(PreInstrument).Single();
		var post = capture.ForInstrument(PostInstrument).Single();
		var ratio = capture.ForInstrument(RatioInstrument).Single();

		Assert.True(pre.Value > post.Value, "pre estimate should exceed post when transform stripped content");
		Assert.True(ratio.Value > 0.0 && ratio.Value <= 1.0, $"ratio should be in (0, 1], got {ratio.Value}");
		// Tags include target so dashboards can break down by surface.
		Assert.Equal("AnthropicMessages", pre.Tag("target"));
		Assert.Equal("AnthropicMessages", post.Tag("target"));
		Assert.Equal("AnthropicMessages", ratio.Tag("target"));
	}

	[Fact]
	public async Task EmitsZeroRatio_OnNoOpPipeline()
	{
		using var capture = new MetricsCapture();

		var (client, _, _) = TransformHandlerFactory.Build(
			s => s.AddTopHatRequestTransform<NoOpRequestTransform>(),
			behavior: (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

		using var req = AnthropicPost("""{"model":"x","stream":false,"messages":[]}""");
		using var _ = await client.SendAsync(req);

		var pre = capture.ForInstrument(PreInstrument).Single();
		var post = capture.ForInstrument(PostInstrument).Single();
		var ratio = capture.ForInstrument(RatioInstrument).Single();

		// Body unchanged → pre == post → ratio == 0.
		Assert.Equal(pre.Value, post.Value);
		Assert.Equal(0.0, ratio.Value);
	}

	[Fact]
	public async Task DoesNotEmitCacheBust_WhenBodyHasNoMarker()
	{
		using var capture = new MetricsCapture();

		var (client, _, _) = TransformHandlerFactory.Build(
			s => s.AddTopHatRequestTransform<ExampleMetadataStripTransform>(),
			behavior: (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

		using var req = AnthropicPost("""{"model":"x","stream":false,"messages":[],"metadata":{"trace":"abc"}}""");
		using var _ = await client.SendAsync(req);

		Assert.Empty(capture.ForInstrument(BustInstrument));
	}

	[Fact]
	public async Task DoesNotEmitCacheBust_WhenTransformDoesNotMutateAboveMarker()
	{
		// Marker is present but the transform only touches content downstream of the marker.
		using var capture = new MetricsCapture();

		var (client, _, _) = TransformHandlerFactory.Build(
			s => s.AddTopHatRequestTransform<ExampleMetadataStripTransform>(),
			behavior: (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

		// Marker comes before metadata in source order — but JSON is order-insensitive at the
		// object level, so what matters is the SERIALIZED order. The compressor strips "metadata"
		// which appears after the marker block in the canonical serialization, so the prefix is
		// untouched.
		var body = $$"""
		{
		    "system": [{ "type": "text", "text": "prompt", "cache_control": { "type": "ephemeral" } }],
		    "model": "x",
		    "stream": false,
		    "messages": [],
		    "metadata": { "trace": "abc" }
		}
		""";
		using var req = AnthropicPost(body);
		using var _ = await client.SendAsync(req);

		// Pre-transform body has the marker; post-transform body still has the same prefix.
		// No bust attribution should occur.
		Assert.Empty(capture.ForInstrument(BustInstrument));
	}

	[Fact]
	public async Task EmitsCacheBust_WhenTransformMutatesAboveMarker()
	{
		// PrefixMutatingTransform strips a top-level field that appears BEFORE the cache_control
		// marker in the canonical JSON serialization. That mutates the prefix → bust.
		using var capture = new MetricsCapture();

		var (client, _, _) = TransformHandlerFactory.Build(
			s => s.AddTopHatRequestTransform<PrefixMutatingTransform>(),
			behavior: (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

		// The "above_marker" field is serialized BEFORE "system" (System.Text.Json preserves
		// declaration order). Removing it shifts the prefix bytes preceding the cache_control
		// marker downstream.
		var body = $$"""
		{
		    "above_marker": "pollute",
		    "system": [{ "type": "text", "text": "prompt", "cache_control": { "type": "ephemeral" } }],
		    "model": "x",
		    "stream": false,
		    "messages": []
		}
		""";
		using var req = AnthropicPost(body);
		using var _ = await client.SendAsync(req);

		var bust = capture.ForInstrument(BustInstrument).Single();
		Assert.Equal(1.0, bust.Value);
		Assert.Equal("AnthropicMessages", bust.Tag("target"));
		Assert.Equal(nameof(PrefixMutatingTransform), bust.Tag("transform_name"));
	}

	[Fact]
	public async Task EmitsCacheBust_OnOpenAIChat_WhenTransformMutatesPrefixMessage()
	{
		// OpenAI cache-prefix detection: anything modified outside the LAST messages[] entry is
		// a potential bust. Here a transform mutates the system message (which is in the prefix),
		// while keeping the last user-turn untouched. Pipeline should attribute the bust correctly.
		using var capture = new MetricsCapture();

		var (client, _, _) = TransformHandlerFactory.Build(
			s => s.AddTopHatRequestTransform<OpenAISystemMutatingTransform>(),
			behavior: (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

		using var req = OpenAIChatPost("""
			{
				"model":"gpt-4o",
				"stream":false,
				"messages":[
					{"role":"system","content":"original system"},
					{"role":"user","content":"first turn"},
					{"role":"user","content":"latest turn"}
				]
			}
			""");
		using var _ = await client.SendAsync(req);

		var bust = capture.ForInstrument(BustInstrument).Single();
		Assert.Equal(1.0, bust.Value);
		Assert.Equal("OpenAIChatCompletions", bust.Tag("target"));
		Assert.Equal(nameof(OpenAISystemMutatingTransform), bust.Tag("transform_name"));
	}

	[Fact]
	public async Task DoesNotEmitCacheBust_OnOpenAIChat_WhenMutationConfinedToLastMessage()
	{
		// Mutating ONLY the last message is benign — that's the "new turn" position which is
		// expected to differ across requests; no cached prefix to bust.
		using var capture = new MetricsCapture();

		var (client, _, _) = TransformHandlerFactory.Build(
			s => s.AddTopHatRequestTransform<OpenAILastMessageMutatingTransform>(),
			behavior: (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

		using var req = OpenAIChatPost("""
			{
				"model":"gpt-4o",
				"stream":false,
				"messages":[
					{"role":"system","content":"original system"},
					{"role":"user","content":"latest turn to mutate"}
				]
			}
			""");
		using var _ = await client.SendAsync(req);

		Assert.Empty(capture.ForInstrument(BustInstrument));
	}

	/// <summary>
	/// Test-only transform that strips a top-level <c>above_marker</c> field. Used to reliably
	/// produce a cache-prefix mutation when the body has a <c>cache_control</c> marker further
	/// down in the JSON.
	/// </summary>
	internal sealed class PrefixMutatingTransform : IRequestTransform
	{
		public ValueTask InvokeAsync(RequestTransformContext context, CancellationToken cancellationToken)
		{
			if (context.Body is not JsonObject obj)
			{
				return ValueTask.CompletedTask;
			}

			if (obj.Remove("above_marker"))
			{
				context.MarkMutated();
			}
			return ValueTask.CompletedTask;
		}
	}

	/// <summary>
	/// Test-only transform that mutates the FIRST message's content (the system message).
	/// This is in the OpenAI cache prefix and should trigger bust detection.
	/// </summary>
	internal sealed class OpenAISystemMutatingTransform : IRequestTransform
	{
		public ValueTask InvokeAsync(RequestTransformContext context, CancellationToken cancellationToken)
		{
			if (context.Body is JsonObject obj
				&& obj["messages"] is JsonArray messages
				&& messages.Count > 0
				&& messages[0] is JsonObject firstMsg)
			{
				firstMsg["content"] = "MUTATED system";
				context.MarkMutated();
			}
			return ValueTask.CompletedTask;
		}
	}

	/// <summary>
	/// Test-only transform that mutates the LAST message only. This is OUTSIDE the OpenAI
	/// cache prefix (the trailing turn is expected to differ) and should NOT trigger bust
	/// detection.
	/// </summary>
	internal sealed class OpenAILastMessageMutatingTransform : IRequestTransform
	{
		public ValueTask InvokeAsync(RequestTransformContext context, CancellationToken cancellationToken)
		{
			if (context.Body is JsonObject obj
				&& obj["messages"] is JsonArray messages
				&& messages.Count > 0
				&& messages[messages.Count - 1] is JsonObject lastMsg)
			{
				lastMsg["content"] = "MUTATED last";
				context.MarkMutated();
			}
			return ValueTask.CompletedTask;
		}
	}
}
