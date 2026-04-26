using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TopHat.Compression.CCR;
using Xunit;

namespace TopHat.Tests.Compression.CCR;

public sealed class AnthropicCCROrchestratorTests
{
	private const string RequestBody = """
		{
			"model": "claude-opus-4-5",
			"messages": [
				{ "role": "user", "content": "what broke?" },
				{ "role": "assistant", "content": [{ "type": "tool_use", "id": "tu_1", "name": "get_logs", "input": {} }] },
				{ "role": "user", "content": [{ "type": "tool_result", "tool_use_id": "tu_1", "content": "[...compressed payload...]" }] }
			],
			"tools": [{ "name": "tophat_retrieve" }]
		}
		""";

	private static (AnthropicCCROrchestrator Orchestrator, InMemoryCompressionContextStore Store) BuildOrchestrator(CCROptions? opts = null)
	{
		var options = Options.Create(opts ?? new CCROptions());
		var store = new InMemoryCompressionContextStore(options);
		return (new AnthropicCCROrchestrator(store, options), store);
	}

	private static HttpRequestMessage Request() => new (HttpMethod.Post, "https://api.anthropic.com/v1/messages")
	{
		Content = new StringContent(RequestBody, Encoding.UTF8, "application/json"),
	};

	private static HttpResponseMessage JsonResponse(string body) => new (HttpStatusCode.OK)
	{
		Content = new StringContent(body, Encoding.UTF8, "application/json"),
	};

	private static string ResponseWithRetrievalToolUse(string retrievalKey, string toolUseId = "toolu_1", int[]? ids = null) =>
		$$"""
		{
			"id": "msg_01",
			"type": "message",
			"role": "assistant",
			"content": [
				{ "type": "text", "text": "Let me check." },
				{
					"type": "tool_use",
					"id": "{{toolUseId}}",
					"name": "tophat_retrieve",
					"input": { "retrieval_key": "{{retrievalKey}}"{{(ids is null ? "" : $", \"ids\": [{string.Join(",", ids)}]")}} }
				}
			],
			"stop_reason": "tool_use"
		}
		""";

	private static string FinalAnswerResponse(string text = "The error is foo.") =>
		$$"""
		{
			"id": "msg_02",
			"type": "message",
			"role": "assistant",
			"content": [{ "type": "text", "text": "{{text}}" }],
			"stop_reason": "end_turn"
		}
		""";

	[Fact]
	public async Task NoToolUse_ReturnsInitialResponseUnchanged()
	{
		var (orchestrator, _) = BuildOrchestrator();
		using var request = Request();
		var initial = JsonResponse(FinalAnswerResponse());
		var context = new CCROrchestrationContext(request, initial, (_, _) => throw new InvalidOperationException("should not dispatch"), "test", NullLogger.Instance);

		var result = await orchestrator.OrchestrateAsync(context, CancellationToken.None);

		Assert.Same(initial, result);
	}

	[Fact]
	public async Task RetrievalToolUse_FulfilsAndReturnsFollowUp()
	{
		var (orchestrator, store) = BuildOrchestrator();
		var key = "abc123";
		store.Store(key, new JsonNode[]
		{
			new JsonObject { ["id"] = 7, ["message"] = "token expired" },
			new JsonObject { ["id"] = 42, ["message"] = "bad credentials" },
		});

		HttpRequestMessage? capturedFollowUp = null;
		string? capturedFollowUpBody = null;

		using var request = Request();
		var initial = JsonResponse(ResponseWithRetrievalToolUse(key, ids: new[] { 7, 42 }));
		var context = new CCROrchestrationContext(request, initial, async (req, _) =>
		{
			capturedFollowUp = req;
			capturedFollowUpBody = await req.Content!.ReadAsStringAsync();
			return JsonResponse(FinalAnswerResponse("Found tokens 7, 42."));
		}, "test", NullLogger.Instance);

		var result = await orchestrator.OrchestrateAsync(context, CancellationToken.None);

		Assert.NotNull(capturedFollowUp);
		var followUpBody = JsonNode.Parse(capturedFollowUpBody!)!.AsObject();
		var messages = followUpBody["messages"]!.AsArray();

		// Original 3 messages + assistant (tool_use) + user (tool_result) = 5
		Assert.Equal(5, messages.Count);

		// Assistant turn echoes the tool_use content.
		var assistantTurn = messages[3]!.AsObject();
		Assert.Equal("assistant", assistantTurn["role"]!.GetValue<string>());
		Assert.NotNull(assistantTurn["content"]);

		// Tool-result turn has the retrieved items as a JSON-serialized array string.
		var toolResultTurn = messages[4]!.AsObject();
		Assert.Equal("user", toolResultTurn["role"]!.GetValue<string>());
		var toolResultBlock = toolResultTurn["content"]!.AsArray()[0]!.AsObject();
		Assert.Equal("tool_result", toolResultBlock["type"]!.GetValue<string>());
		Assert.Equal("toolu_1", toolResultBlock["tool_use_id"]!.GetValue<string>());

		var retrievedJson = JsonNode.Parse(toolResultBlock["content"]!.GetValue<string>())!.AsArray();
		Assert.Equal(2, retrievedJson.Count);
		Assert.Equal(7, retrievedJson[0]!["id"]!.GetValue<int>());
		Assert.Equal(42, retrievedJson[1]!["id"]!.GetValue<int>());

		// The returned response is the follow-up's terminal answer, not the initial tool_use response.
		var finalBody = JsonNode.Parse(await result.Content.ReadAsStringAsync())!.AsObject();
		Assert.Equal("end_turn", finalBody["stop_reason"]!.GetValue<string>());
	}

	[Fact]
	public async Task ForeignToolUseAlongsideRetrieval_PassesThroughUnchanged()
	{
		var (orchestrator, store) = BuildOrchestrator();
		store.Store("k", new JsonNode[] { new JsonObject { ["id"] = 1 } });

		var mixed = $$"""
		{
			"id": "msg_01",
			"content": [
				{ "type": "tool_use", "id": "tu_1", "name": "tophat_retrieve", "input": { "retrieval_key": "k" } },
				{ "type": "tool_use", "id": "tu_2", "name": "user_defined_tool", "input": {} }
			],
			"stop_reason": "tool_use"
		}
		""";

		using var request = Request();
		var initial = JsonResponse(mixed);
		var context = new CCROrchestrationContext(request, initial, (_, _) => throw new InvalidOperationException("should not dispatch"), "test", NullLogger.Instance);

		var result = await orchestrator.OrchestrateAsync(context, CancellationToken.None);

		Assert.Same(initial, result);
	}

	[Fact]
	public async Task ErrorStatusResponse_PassesThrough()
	{
		var (orchestrator, _) = BuildOrchestrator();
		using var request = Request();
		var error = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
		{
			Content = new StringContent("""{"error":"rate_limited"}""", Encoding.UTF8, "application/json"),
		};
		var context = new CCROrchestrationContext(request, error, (_, _) => throw new InvalidOperationException(), "test", NullLogger.Instance);

		var result = await orchestrator.OrchestrateAsync(context, CancellationToken.None);

		Assert.Same(error, result);
	}

	[Fact]
	public async Task StreamingContentType_PassesThrough()
	{
		var (orchestrator, _) = BuildOrchestrator();
		using var request = Request();
		var streaming = new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("event: message_start\ndata: {}\n\n", Encoding.UTF8, "text/event-stream"),
		};
		var context = new CCROrchestrationContext(request, streaming, (_, _) => throw new InvalidOperationException(), "test", NullLogger.Instance);

		var result = await orchestrator.OrchestrateAsync(context, CancellationToken.None);

		Assert.Same(streaming, result);
	}

	[Fact]
	public async Task MaxIterations_StopsLoopingAndReturnsLastResponse()
	{
		var (orchestrator, store) = BuildOrchestrator(new CCROptions { MaxIterations = 2 });
		store.Store("k", new JsonNode[] { new JsonObject { ["id"] = 1 } });

		var callCount = 0;

		using var request = Request();
		var initial = JsonResponse(ResponseWithRetrievalToolUse("k"));
		var context = new CCROrchestrationContext(request, initial, (_, _) =>
		{
			callCount++;
			// Every follow-up also contains another retrieval tool_use — pathological model behavior.
			return Task.FromResult(JsonResponse(ResponseWithRetrievalToolUse("k", toolUseId: $"toolu_{callCount + 1}")));
		}, "test", NullLogger.Instance);

		var result = await orchestrator.OrchestrateAsync(context, CancellationToken.None);

		// MaxIterations = 2 → processes initial + 1 follow-up, then hits budget cap on iteration 2.
		Assert.Equal(2, callCount);

		// The final response still has the pathological tool_use — orchestrator didn't silently
		// sanitize it. The client sees it as an "unknown tool" failure; terminal and observable.
		var finalBody = JsonNode.Parse(await result.Content.ReadAsStringAsync())!.AsObject();
		Assert.Equal("tool_use", finalBody["stop_reason"]!.GetValue<string>());
	}

	[Fact]
	public async Task MultiHop_RewritesUsageCumulativelyAndStampsHopHeader()
	{
		// Two hops total: initial response has usage 100/20; follow-up has 150/30. The user sees
		// a final response whose usage reflects the sum (250/50) — matches what they were billed.
		var (orchestrator, store) = BuildOrchestrator();
		store.Store("k", new JsonNode[] { new JsonObject { ["id"] = 1 } });

		using var request = Request();
		var initial = JsonResponse($$"""
			{
				"id": "msg_01",
				"type": "message",
				"role": "assistant",
				"content": [
					{ "type": "tool_use", "id": "toolu_1", "name": "tophat_retrieve", "input": { "retrieval_key": "k" } }
				],
				"stop_reason": "tool_use",
				"usage": { "input_tokens": 100, "output_tokens": 20, "cache_read_input_tokens": 50 }
			}
			""");
		var context = new CCROrchestrationContext(request, initial, (_, _) =>
		{
			var followUp = JsonResponse("""
				{
					"id": "msg_02",
					"content": [{ "type": "text", "text": "Answered." }],
					"stop_reason": "end_turn",
					"usage": { "input_tokens": 150, "output_tokens": 30, "cache_read_input_tokens": 25 }
				}
				""");
			return Task.FromResult(followUp);
		}, "test", NullLogger.Instance);

		var result = await orchestrator.OrchestrateAsync(context, CancellationToken.None);

		Assert.Equal("2", result.Headers.GetValues(CCRUsageMerger.HopCountHeader).Single());
		var finalBody = JsonNode.Parse(await result.Content.ReadAsStringAsync())!.AsObject();
		Assert.Equal(250, finalBody["usage"]!["input_tokens"]!.GetValue<long>());
		Assert.Equal(50, finalBody["usage"]!["output_tokens"]!.GetValue<long>());
		Assert.Equal(75, finalBody["usage"]!["cache_read_input_tokens"]!.GetValue<long>());
	}

	[Fact]
	public async Task SingleHop_LeavesResponseUntouched()
	{
		// When the initial response is already terminal (no tool_use), the orchestrator must not
		// rewrite usage or stamp the hop header. The response is passed through as-is.
		var (orchestrator, _) = BuildOrchestrator();
		using var request = Request();
		var initialBody = """
			{
				"id": "msg_01",
				"content": [{ "type": "text", "text": "done" }],
				"stop_reason": "end_turn",
				"usage": { "input_tokens": 42, "output_tokens": 7 }
			}
			""";
		var initial = JsonResponse(initialBody);
		var context = new CCROrchestrationContext(request, initial, (_, _) => throw new InvalidOperationException("should not dispatch"), "test", NullLogger.Instance);

		var result = await orchestrator.OrchestrateAsync(context, CancellationToken.None);

		Assert.False(result.Headers.Contains(CCRUsageMerger.HopCountHeader));
		var finalBody = JsonNode.Parse(await result.Content.ReadAsStringAsync())!.AsObject();
		Assert.Equal(42, finalBody["usage"]!["input_tokens"]!.GetValue<long>());
		Assert.Equal(7, finalBody["usage"]!["output_tokens"]!.GetValue<long>());
	}

	[Fact]
	public async Task InvalidRetrievalKey_ReturnsEmptyArrayInToolResult()
	{
		var (orchestrator, _) = BuildOrchestrator();

		string? capturedBody = null;

		using var request = Request();
		var initial = JsonResponse(ResponseWithRetrievalToolUse("nonexistent"));
		var context = new CCROrchestrationContext(request, initial, async (req, _) =>
		{
			capturedBody = await req.Content!.ReadAsStringAsync();
			return JsonResponse(FinalAnswerResponse());
		}, "test", NullLogger.Instance);

		await orchestrator.OrchestrateAsync(context, CancellationToken.None);

		var body = JsonNode.Parse(capturedBody!)!.AsObject();
		var messages = body["messages"]!.AsArray();
		var toolResult = messages[4]!.AsObject()["content"]!.AsArray()[0]!.AsObject();
		var resultPayload = JsonNode.Parse(toolResult["content"]!.GetValue<string>())!.AsArray();

		Assert.Empty(resultPayload);
	}
}
