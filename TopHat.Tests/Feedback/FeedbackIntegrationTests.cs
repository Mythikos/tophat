using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TopHat.DependencyInjection;
using TopHat.Feedback;
using TopHat.Relevance.BM25.DependencyInjection;
using TopHat.Tests.Support;
using Xunit;

namespace TopHat.Tests.Feedback;

/// <summary>
/// End-to-end integration: drive a request through the full pipeline, verify feedback
/// recording + decision consultation behaves as designed.
/// </summary>
public sealed class FeedbackIntegrationTests
{
	private static string BigJsonArray(int count = 100) =>
		System.Text.Json.JsonSerializer.Serialize(
			Enumerable.Range(1, count).Select(i =>
				new { id = i, status = "ok", message = "record processed successfully" })
			.ToArray());

	private static HttpRequestMessage AnthropicRequest(string toolResultContent) =>
		new(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
		{
			Content = new StringContent($$"""
				{
					"model": "claude-haiku-4-5",
					"max_tokens": 100,
					"messages": [
						{ "role": "user", "content": "summarize logs" },
						{ "role": "assistant", "content": [{ "type": "tool_use", "id": "tu_1", "name": "get_logs", "input": {} }] },
						{ "role": "user", "content": [{ "type": "tool_result", "tool_use_id": "tu_1", "content": {{System.Text.Json.JsonSerializer.Serialize(toolResultContent)}} }] }
					]
				}
				""", Encoding.UTF8, "application/json"),
		};

	[Fact]
	public async Task CompressionEvent_IsRecordedKeyedByToolName()
	{
		ICompressionFeedbackStore? store = null;

		var (client, _, sp) = TransformHandlerFactory.Build(
			services =>
			{
				services.AddTopHatBm25Relevance();
				services.AddTopHatJsonContextCompressor();
			},
			behavior: (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

		store = sp.GetRequiredService<ICompressionFeedbackStore>();
		Assert.IsType<InMemoryCompressionFeedbackStore>(store);

		await client.SendAsync(AnthropicRequest(BigJsonArray()));

		// Compressor should have keyed the event by the tool name from the conversation.
		var stats = store.GetStats("get_logs");
		Assert.NotNull(stats);
		Assert.Equal(1, stats.TotalCompressions);
	}

	[Fact]
	public async Task ManualOverrideSkipCompression_SkipsCompressionEntirely()
	{
		// Pre-seed the store with a manual SkipCompression override for get_logs.
		// Verify the captured upstream request body's tool_result is the ORIGINAL content
		// — not compressed, no _tophat_compression metadata.
		JsonObject? capturedBody = null;

		var (client, _, sp) = TransformHandlerFactory.Build(
			services =>
			{
				services.AddTopHatBm25Relevance();
				services.AddTopHatJsonContextCompressor();
				services.UseTopHatFeedbackDecisions();
			},
			behavior: async (req, _) =>
			{
				capturedBody = JsonNode.Parse(await req.Content!.ReadAsByteArrayAsync())!.AsObject();
				return new HttpResponseMessage(HttpStatusCode.OK);
			});

		var store = sp.GetRequiredService<ICompressionFeedbackStore>();
		store.SetManualOverride("get_logs", FeedbackOverride.SkipCompression);

		var originalContent = BigJsonArray();
		await client.SendAsync(AnthropicRequest(originalContent));

		// Body should pass through unchanged — same content the eval sent.
		Assert.NotNull(capturedBody);
		var toolResult = capturedBody["messages"]!.AsArray()[2]!.AsObject()["content"]!.AsArray()[0]!.AsObject();
		var content = toolResult["content"]!.GetValue<string>();
		Assert.Equal(originalContent, content);
	}

	[Fact]
	public async Task LearnedSkipCompression_SkipsAfterEnoughSamples()
	{
		// Seed stats with high full-retrieval rate. Decision layer should skip compression
		// based purely on the empirical data — no manual override needed.
		JsonObject? capturedBody = null;

		var (client, _, sp) = TransformHandlerFactory.Build(
			services =>
			{
				services.AddTopHatBm25Relevance();
				services.AddTopHatJsonContextCompressor();
				services.UseTopHatFeedbackDecisions();
			},
			behavior: async (req, _) =>
			{
				capturedBody = JsonNode.Parse(await req.Content!.ReadAsByteArrayAsync())!.AsObject();
				return new HttpResponseMessage(HttpStatusCode.OK);
			});

		var store = sp.GetRequiredService<ICompressionFeedbackStore>();
		// 10 compressions, 9 retrievals all full → 90% retrieval, 100% full → both thresholds exceeded.
		store.SeedStats("get_logs", totalCompressions: 10, totalRetrievals: 9, fullRetrievals: 9, searchRetrievals: 0, budgetExhausted: 0);

		var originalContent = BigJsonArray();
		await client.SendAsync(AnthropicRequest(originalContent));

		var toolResult = capturedBody!["messages"]!.AsArray()[2]!.AsObject()["content"]!.AsArray()[0]!.AsObject();
		Assert.Equal(originalContent, toolResult["content"]!.GetValue<string>());
	}

	[Fact]
	public async Task ManualOverrideSkip_HonoredEvenWithoutDecisions()
	{
		// Manual override is user-declared truth and applies unconditionally — no need to
		// also enable UseTopHatFeedbackDecisions() to opt specific tools out of compression.
		// This separates "I know this tool's data needs full inspection" from "let TopHat
		// learn empirically what to skip."
		JsonObject? capturedBody = null;

		var (client, _, sp) = TransformHandlerFactory.Build(
			services =>
			{
				services.AddTopHatBm25Relevance();
				services.AddTopHatJsonContextCompressor();
				// Note: NO UseTopHatFeedbackDecisions() — empirical learning stays off.
			},
			behavior: async (req, _) =>
			{
				capturedBody = JsonNode.Parse(await req.Content!.ReadAsByteArrayAsync())!.AsObject();
				return new HttpResponseMessage(HttpStatusCode.OK);
			});

		var store = sp.GetRequiredService<ICompressionFeedbackStore>();
		store.SetManualOverride("get_logs", FeedbackOverride.SkipCompression);

		var originalContent = BigJsonArray();
		await client.SendAsync(AnthropicRequest(originalContent));

		// Override fires even with decisions off → compression skipped → body passes through unchanged.
		var toolResult = capturedBody!["messages"]!.AsArray()[2]!.AsObject()["content"]!.AsArray()[0]!.AsObject();
		var content = toolResult["content"]!.GetValue<string>();
		Assert.Equal(originalContent, content);
	}

	[Fact]
	public async Task EmpiricalDecisions_StillRequireOptIn()
	{
		// Sanity check the OTHER direction: high-skip-worthy STATS without a manual override
		// should NOT skip compression unless UseTopHatFeedbackDecisions() is enabled. This is
		// the empirical learning layer that should stay opt-in.
		JsonObject? capturedBody = null;

		var (client, _, sp) = TransformHandlerFactory.Build(
			services =>
			{
				services.AddTopHatBm25Relevance();
				services.AddTopHatJsonContextCompressor();
				// Note: NO UseTopHatFeedbackDecisions() — empirical layer stays off.
			},
			behavior: async (req, _) =>
			{
				capturedBody = JsonNode.Parse(await req.Content!.ReadAsByteArrayAsync())!.AsObject();
				return new HttpResponseMessage(HttpStatusCode.OK);
			});

		var store = sp.GetRequiredService<ICompressionFeedbackStore>();
		// Seed pathological stats — would normally trigger skip if decisions were on.
		store.SeedStats("get_logs", totalCompressions: 100, totalRetrievals: 100, fullRetrievals: 100, searchRetrievals: 0, budgetExhausted: 0);

		var originalContent = BigJsonArray();
		await client.SendAsync(AnthropicRequest(originalContent));

		// Decisions OFF → empirical signal ignored → compression happens.
		var toolResult = capturedBody!["messages"]!.AsArray()[2]!.AsObject()["content"]!.AsArray()[0]!.AsObject();
		var content = toolResult["content"]!.GetValue<string>();
		Assert.True(content.Length < originalContent.Length, "compression should have happened — empirical learning is opt-in");
	}

	[Fact]
	public async Task UseTopHatFeedbackOverrides_StaticDeclaration_SkipsCompression()
	{
		// Declarative startup-time overrides. No runtime SetManualOverride call needed and
		// no UseTopHatFeedbackDecisions() either — pure config, applied at request time.
		JsonObject? capturedBody = null;

		var (client, _, _) = TransformHandlerFactory.Build(
			services =>
			{
				services.AddTopHatBm25Relevance();
				services.AddTopHatJsonContextCompressor();
				services.UseTopHatFeedbackOverrides(opt => opt.SkipCompressionFor("get_logs"));
			},
			behavior: async (req, _) =>
			{
				capturedBody = JsonNode.Parse(await req.Content!.ReadAsByteArrayAsync())!.AsObject();
				return new HttpResponseMessage(HttpStatusCode.OK);
			});

		var originalContent = BigJsonArray();
		await client.SendAsync(AnthropicRequest(originalContent));

		var toolResult = capturedBody!["messages"]!.AsArray()[2]!.AsObject()["content"]!.AsArray()[0]!.AsObject();
		Assert.Equal(originalContent, toolResult["content"]!.GetValue<string>());
	}

	[Fact]
	public async Task StoreOverride_WinsOverConfigOverride()
	{
		// Precedence test: config says skip, store says always-compress → store wins (runtime intent).
		JsonObject? capturedBody = null;

		var (client, _, sp) = TransformHandlerFactory.Build(
			services =>
			{
				services.AddTopHatBm25Relevance();
				services.AddTopHatJsonContextCompressor();
				services.UseTopHatFeedbackOverrides(opt => opt.SkipCompressionFor("get_logs"));
			},
			behavior: async (req, _) =>
			{
				capturedBody = JsonNode.Parse(await req.Content!.ReadAsByteArrayAsync())!.AsObject();
				return new HttpResponseMessage(HttpStatusCode.OK);
			});

		var store = sp.GetRequiredService<ICompressionFeedbackStore>();
		store.SetManualOverride("get_logs", FeedbackOverride.AlwaysCompress);

		var originalContent = BigJsonArray();
		await client.SendAsync(AnthropicRequest(originalContent));

		// Store override wins → compression happens despite config saying skip.
		var toolResult = capturedBody!["messages"]!.AsArray()[2]!.AsObject()["content"]!.AsArray()[0]!.AsObject();
		var content = toolResult["content"]!.GetValue<string>();
		Assert.True(content.Length < originalContent.Length, "store AlwaysCompress should override config skip");
	}

	[Fact]
	public async Task UseTopHatNoopFeedbackStore_DropsAllRecording()
	{
		var (client, _, sp) = TransformHandlerFactory.Build(
			services =>
			{
				services.AddTopHatBm25Relevance();
				services.AddTopHatJsonContextCompressor();
				services.UseTopHatNoopFeedbackStore();
			},
			behavior: (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

		var store = sp.GetRequiredService<ICompressionFeedbackStore>();
		Assert.IsType<NullCompressionFeedbackStore>(store);

		await client.SendAsync(AnthropicRequest(BigJsonArray()));

		Assert.Null(store.GetStats("get_logs"));
	}
}
