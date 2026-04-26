using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TopHat.Compression.CCR;
using TopHat.Tests.Support;
using Xunit;

namespace TopHat.Tests.Compression.CCR;

/// <summary>
/// Validates that CCR orchestrators emit OpenTelemetry instruments correctly. Uses
/// <see cref="MetricsCapture"/> to subscribe to the TopHat Meter and assert that the right
/// instruments fired with the right tags. Outcome labels here form a contract — dashboards
/// downstream depend on these specific strings.
/// </summary>
public sealed class CCRMetricsTests
{
	private const string OrchestrationsInstrument = "tophat.ccr.orchestrations";
	private const string HopsInstrument = "tophat.ccr.hops";

	private static (AnthropicCCROrchestrator Orchestrator, InMemoryCompressionContextStore Store) BuildAnthropic(CCROptions? opts = null)
	{
		var options = Options.Create(opts ?? new CCROptions());
		var store = new InMemoryCompressionContextStore(options);
		return (new AnthropicCCROrchestrator(store, options), store);
	}

	private static HttpRequestMessage AnthropicRequest() => new(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
	{
		Content = new StringContent("""
			{
				"model": "claude-opus-4-5",
				"messages": [
					{ "role": "user", "content": "what?" },
					{ "role": "assistant", "content": [{ "type": "tool_use", "id": "tu_1", "name": "get_logs", "input": {} }] },
					{ "role": "user", "content": [{ "type": "tool_result", "tool_use_id": "tu_1", "content": "[...]" }] }
				]
			}
			""", Encoding.UTF8, "application/json"),
	};

	private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
	{
		Content = new StringContent(body, Encoding.UTF8, "application/json"),
	};

	[Fact]
	public async Task SingleHop_EmitsSingleHopOutcomeAndHopCountOf1()
	{
		using var capture = new MetricsCapture();

		var (orchestrator, _) = BuildAnthropic();
		using var request = AnthropicRequest();
		var initial = JsonResponse("""
			{ "id":"msg_01", "content":[{"type":"text","text":"done"}], "stop_reason":"end_turn" }
			""");
		var context = new CCROrchestrationContext(request, initial, (_, _) => throw new InvalidOperationException(), "t", NullLogger.Instance);

		await orchestrator.OrchestrateAsync(context, CancellationToken.None);

		var orchestrations = capture.ForInstrument(OrchestrationsInstrument).Single();
		Assert.Equal(1.0, orchestrations.Value);
		Assert.Equal("AnthropicMessages", orchestrations.Tag("target"));
		Assert.Equal("single_hop", orchestrations.Tag("outcome"));

		var hops = capture.ForInstrument(HopsInstrument).Single();
		Assert.Equal(1.0, hops.Value);
		Assert.Equal("AnthropicMessages", hops.Tag("target"));
	}

	[Fact]
	public async Task MultiHop_EmitsMultiHopOutcomeAndHopCountMatchingActualHops()
	{
		using var capture = new MetricsCapture();

		var (orchestrator, store) = BuildAnthropic();
		store.Store("k", new JsonNode[] { new JsonObject { ["id"] = 1 } });

		using var request = AnthropicRequest();
		var initial = JsonResponse("""
			{
				"id":"msg_01",
				"content":[{"type":"tool_use","id":"toolu_1","name":"tophat_retrieve","input":{"retrieval_key":"k"}}],
				"stop_reason":"tool_use"
			}
			""");
		var context = new CCROrchestrationContext(request, initial, (_, _) =>
		{
			var followUp = JsonResponse("""
				{ "id":"msg_02", "content":[{"type":"text","text":"done"}], "stop_reason":"end_turn" }
				""");
			return Task.FromResult(followUp);
		}, "t", NullLogger.Instance);

		await orchestrator.OrchestrateAsync(context, CancellationToken.None);

		var orchestrations = capture.ForInstrument(OrchestrationsInstrument).Single();
		Assert.Equal("multi_hop", orchestrations.Tag("outcome"));

		var hops = capture.ForInstrument(HopsInstrument).Single();
		Assert.Equal(2.0, hops.Value);
	}

	[Fact]
	public async Task ForeignToolUse_EmitsForeignToolUseOutcome()
	{
		using var capture = new MetricsCapture();

		var (orchestrator, store) = BuildAnthropic();
		store.Store("k", new JsonNode[] { new JsonObject { ["id"] = 1 } });

		using var request = AnthropicRequest();
		var mixed = """
			{
				"id":"msg_01",
				"content":[
					{"type":"tool_use","id":"tu_1","name":"tophat_retrieve","input":{"retrieval_key":"k"}},
					{"type":"tool_use","id":"tu_2","name":"user_tool","input":{}}
				],
				"stop_reason":"tool_use"
			}
			""";
		var context = new CCROrchestrationContext(request, JsonResponse(mixed), (_, _) => throw new InvalidOperationException(), "t", NullLogger.Instance);

		await orchestrator.OrchestrateAsync(context, CancellationToken.None);

		var orchestrations = capture.ForInstrument(OrchestrationsInstrument).Single();
		Assert.Equal("foreign_tool_use", orchestrations.Tag("outcome"));
	}

	[Fact]
	public async Task BudgetExhausted_EmitsBudgetExhaustedOutcomeAndFullHopCount()
	{
		using var capture = new MetricsCapture();

		var (orchestrator, store) = BuildAnthropic(new CCROptions { MaxIterations = 2 });
		store.Store("k", new JsonNode[] { new JsonObject { ["id"] = 1 } });

		using var request = AnthropicRequest();
		var pathological = """
			{
				"id":"msg_x",
				"content":[{"type":"tool_use","id":"toolu_x","name":"tophat_retrieve","input":{"retrieval_key":"k"}}],
				"stop_reason":"tool_use"
			}
			""";
		var initial = JsonResponse(pathological);
		var context = new CCROrchestrationContext(request, initial, (_, _) => Task.FromResult(JsonResponse(pathological)), "t", NullLogger.Instance);

		await orchestrator.OrchestrateAsync(context, CancellationToken.None);

		var orchestrations = capture.ForInstrument(OrchestrationsInstrument).Single();
		Assert.Equal("budget_exhausted", orchestrations.Tag("outcome"));

		var hops = capture.ForInstrument(HopsInstrument).Single();
		// MaxIterations=2 → initial + 1 follow-up + bumped hopCount past last dispatch = 3.
		Assert.Equal(3.0, hops.Value);
	}

	[Fact]
	public async Task NotOrchestratable_EmitsNotOrchestratableOutcome()
	{
		using var capture = new MetricsCapture();

		var (orchestrator, _) = BuildAnthropic();
		using var request = AnthropicRequest();
		var error = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
		{
			Content = new StringContent("""{"error":"rate_limited"}""", Encoding.UTF8, "application/json"),
		};
		var context = new CCROrchestrationContext(request, error, (_, _) => throw new InvalidOperationException(), "t", NullLogger.Instance);

		await orchestrator.OrchestrateAsync(context, CancellationToken.None);

		var orchestrations = capture.ForInstrument(OrchestrationsInstrument).Single();
		Assert.Equal("not_orchestratable", orchestrations.Tag("outcome"));
	}

	[Fact]
	public async Task OpenAIChatOrchestrator_TagsTargetCorrectly()
	{
		using var capture = new MetricsCapture();

		var options = Options.Create(new CCROptions());
		var store = new InMemoryCompressionContextStore(options);
		var orchestrator = new OpenAIChatCompletionsCCROrchestrator(store, options);

		using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
		{
			Content = new StringContent("""
				{
					"model":"gpt-4o",
					"messages":[
						{"role":"user","content":"q"},
						{"role":"tool","tool_call_id":"call_1","content":"[]"}
					]
				}
				""", Encoding.UTF8, "application/json"),
		};
		var initial = JsonResponse("""
			{ "id":"chatcmpl_01", "choices":[{"index":0,"message":{"role":"assistant","content":"done"},"finish_reason":"stop"}] }
			""");
		var context = new CCROrchestrationContext(request, initial, (_, _) => throw new InvalidOperationException(), "t", NullLogger.Instance);

		await orchestrator.OrchestrateAsync(context, CancellationToken.None);

		var orchestrations = capture.ForInstrument(OrchestrationsInstrument).Single();
		Assert.Equal("OpenAIChatCompletions", orchestrations.Tag("target"));
		Assert.Equal("single_hop", orchestrations.Tag("outcome"));
	}

	[Fact]
	public async Task OpenAIResponsesOrchestrator_TagsTargetCorrectly()
	{
		using var capture = new MetricsCapture();

		var options = Options.Create(new CCROptions());
		var store = new InMemoryCompressionContextStore(options);
		var orchestrator = new OpenAIResponsesCCROrchestrator(store, options);

		using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses")
		{
			Content = new StringContent("""
				{
					"model":"gpt-4o",
					"input":[
						{"role":"user","content":"q"},
						{"type":"function_call_output","call_id":"fc_1","output":"[]"}
					]
				}
				""", Encoding.UTF8, "application/json"),
		};
		var initial = JsonResponse("""
			{ "id":"resp_01", "output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"done"}]}], "status":"completed" }
			""");
		var context = new CCROrchestrationContext(request, initial, (_, _) => throw new InvalidOperationException(), "t", NullLogger.Instance);

		await orchestrator.OrchestrateAsync(context, CancellationToken.None);

		var orchestrations = capture.ForInstrument(OrchestrationsInstrument).Single();
		Assert.Equal("OpenAIResponses", orchestrations.Tag("target"));
		Assert.Equal("single_hop", orchestrations.Tag("outcome"));
	}
}
