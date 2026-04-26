using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TopHat.Compression.CCR;
using Xunit;

namespace TopHat.Tests.Compression.CCR;

public sealed class OpenAIChatCompletionsCCROrchestratorTests
{
	private const string RequestBody = """
		{
			"model": "gpt-4o",
			"messages": [
				{ "role": "user", "content": "what broke?" },
				{ "role": "assistant", "tool_calls": [{ "id": "call_logs", "type": "function", "function": { "name": "get_logs", "arguments": "{}" } }] },
				{ "role": "tool", "tool_call_id": "call_logs", "content": "[...compressed payload...]" }
			],
			"tools": [{ "type": "function", "function": { "name": "tophat_retrieve" } }]
		}
		""";

	private static (OpenAIChatCompletionsCCROrchestrator Orchestrator, InMemoryCompressionContextStore Store) BuildOrchestrator(CCROptions? opts = null)
	{
		var options = Options.Create(opts ?? new CCROptions());
		var store = new InMemoryCompressionContextStore(options);
		return (new OpenAIChatCompletionsCCROrchestrator(store, options), store);
	}

	private static HttpRequestMessage Request() => new(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
	{
		Content = new StringContent(RequestBody, Encoding.UTF8, "application/json"),
	};

	private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
	{
		Content = new StringContent(body, Encoding.UTF8, "application/json"),
	};

	private static string ResponseWithRetrievalToolCall(string retrievalKey, string toolCallId = "call_retrieve_1", int[]? ids = null)
	{
		var argsJson = ids is null
			? $"{{\\\"retrieval_key\\\":\\\"{retrievalKey}\\\"}}"
			: $"{{\\\"retrieval_key\\\":\\\"{retrievalKey}\\\",\\\"ids\\\":[{string.Join(",", ids)}]}}";

		return $$"""
			{
				"id": "chatcmpl_01",
				"object": "chat.completion",
				"choices": [{
					"index": 0,
					"message": {
						"role": "assistant",
						"content": null,
						"tool_calls": [{
							"id": "{{toolCallId}}",
							"type": "function",
							"function": { "name": "tophat_retrieve", "arguments": "{{argsJson}}" }
						}]
					},
					"finish_reason": "tool_calls"
				}]
			}
			""";
	}

	private static string FinalAnswerResponse(string text = "All clear.") =>
		$$"""
		{
			"id": "chatcmpl_02",
			"object": "chat.completion",
			"choices": [{
				"index": 0,
				"message": { "role": "assistant", "content": "{{text}}" },
				"finish_reason": "stop"
			}]
		}
		""";

	[Fact]
	public async Task NoToolCall_ReturnsInitialResponseUnchanged()
	{
		var (orchestrator, _) = BuildOrchestrator();
		using var request = Request();
		var initial = JsonResponse(FinalAnswerResponse());
		var context = new CCROrchestrationContext(request, initial, (_, _) => throw new InvalidOperationException("should not dispatch"), "test", NullLogger.Instance);

		var result = await orchestrator.OrchestrateAsync(context, CancellationToken.None);

		Assert.Same(initial, result);
	}

	[Fact]
	public async Task RetrievalToolCall_FulfilsAndReturnsFollowUp()
	{
		var (orchestrator, store) = BuildOrchestrator();
		var key = "abc123";
		store.Store(key, new JsonNode[]
		{
			new JsonObject { ["id"] = 7, ["message"] = "token expired" },
			new JsonObject { ["id"] = 42, ["message"] = "bad credentials" },
		});

		string? capturedFollowUpBody = null;

		using var request = Request();
		var initial = JsonResponse(ResponseWithRetrievalToolCall(key, ids: new[] { 7, 42 }));
		var context = new CCROrchestrationContext(request, initial, async (req, _) =>
		{
			capturedFollowUpBody = await req.Content!.ReadAsStringAsync();
			return JsonResponse(FinalAnswerResponse("Found tokens 7, 42."));
		}, "test", NullLogger.Instance);

		var result = await orchestrator.OrchestrateAsync(context, CancellationToken.None);

		var followUpBody = JsonNode.Parse(capturedFollowUpBody!)!.AsObject();
		var messages = followUpBody["messages"]!.AsArray();

		// Original 3 messages + assistant (with tool_calls) + tool message = 5
		Assert.Equal(5, messages.Count);

		var assistantTurn = messages[3]!.AsObject();
		Assert.Equal("assistant", assistantTurn["role"]!.GetValue<string>());
		var echoedToolCalls = assistantTurn["tool_calls"]!.AsArray();
		Assert.Single(echoedToolCalls);
		Assert.Equal("call_retrieve_1", echoedToolCalls[0]!["id"]!.GetValue<string>());

		var toolTurn = messages[4]!.AsObject();
		Assert.Equal("tool", toolTurn["role"]!.GetValue<string>());
		Assert.Equal("call_retrieve_1", toolTurn["tool_call_id"]!.GetValue<string>());

		var retrieved = JsonNode.Parse(toolTurn["content"]!.GetValue<string>())!.AsArray();
		Assert.Equal(2, retrieved.Count);
		Assert.Equal(7, retrieved[0]!["id"]!.GetValue<int>());
		Assert.Equal(42, retrieved[1]!["id"]!.GetValue<int>());

		var finalBody = JsonNode.Parse(await result.Content.ReadAsStringAsync())!.AsObject();
		Assert.Equal("stop", finalBody["choices"]![0]!["finish_reason"]!.GetValue<string>());
	}

	[Fact]
	public async Task ForeignToolCallAlongsideRetrieval_PassesThroughUnchanged()
	{
		var (orchestrator, store) = BuildOrchestrator();
		store.Store("k", new JsonNode[] { new JsonObject { ["id"] = 1 } });

		var mixed = """
			{
				"id": "chatcmpl_01",
				"choices": [{
					"index": 0,
					"message": {
						"role": "assistant",
						"tool_calls": [
							{ "id": "tu_1", "type": "function", "function": { "name": "tophat_retrieve", "arguments": "{\"retrieval_key\":\"k\"}" } },
							{ "id": "tu_2", "type": "function", "function": { "name": "user_defined_tool", "arguments": "{}" } }
						]
					},
					"finish_reason": "tool_calls"
				}]
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
			Content = new StringContent("data: {}\n\n", Encoding.UTF8, "text/event-stream"),
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
		var initial = JsonResponse(ResponseWithRetrievalToolCall("k"));
		var context = new CCROrchestrationContext(request, initial, (_, _) =>
		{
			callCount++;
			return Task.FromResult(JsonResponse(ResponseWithRetrievalToolCall("k", toolCallId: $"call_retrieve_{callCount + 1}")));
		}, "test", NullLogger.Instance);

		var result = await orchestrator.OrchestrateAsync(context, CancellationToken.None);

		// MaxIterations = 2 → processes initial + 1 follow-up, then hits budget cap.
		Assert.Equal(2, callCount);

		// Final response still pathological — orchestrator didn't sanitize.
		var finalBody = JsonNode.Parse(await result.Content.ReadAsStringAsync())!.AsObject();
		Assert.Equal("tool_calls", finalBody["choices"]![0]!["finish_reason"]!.GetValue<string>());
	}

	[Fact]
	public async Task InvalidRetrievalKey_ReturnsEmptyArrayInToolMessage()
	{
		var (orchestrator, _) = BuildOrchestrator();

		string? capturedBody = null;

		using var request = Request();
		var initial = JsonResponse(ResponseWithRetrievalToolCall("nonexistent"));
		var context = new CCROrchestrationContext(request, initial, async (req, _) =>
		{
			capturedBody = await req.Content!.ReadAsStringAsync();
			return JsonResponse(FinalAnswerResponse());
		}, "test", NullLogger.Instance);

		await orchestrator.OrchestrateAsync(context, CancellationToken.None);

		var body = JsonNode.Parse(capturedBody!)!.AsObject();
		var messages = body["messages"]!.AsArray();
		var toolTurn = messages[4]!.AsObject();
		var retrieved = JsonNode.Parse(toolTurn["content"]!.GetValue<string>())!.AsArray();

		Assert.Empty(retrieved);
	}

	[Fact]
	public async Task MultiHop_RewritesUsageCumulativelyAndStampsHopHeader()
	{
		var (orchestrator, store) = BuildOrchestrator();
		store.Store("k", new JsonNode[] { new JsonObject { ["id"] = 1 } });

		using var request = Request();
		var initialBody = $$"""
			{
				"id": "chatcmpl_01",
				"choices": [{
					"index": 0,
					"message": {
						"role": "assistant",
						"tool_calls": [{
							"id": "call_1",
							"type": "function",
							"function": { "name": "tophat_retrieve", "arguments": "{\"retrieval_key\":\"k\"}" }
						}]
					},
					"finish_reason": "tool_calls"
				}],
				"usage": {
					"prompt_tokens": 100,
					"completion_tokens": 20,
					"total_tokens": 120,
					"prompt_tokens_details": { "cached_tokens": 40 }
				}
			}
			""";
		var initial = JsonResponse(initialBody);
		var context = new CCROrchestrationContext(request, initial, (_, _) =>
		{
			var followUp = JsonResponse("""
				{
					"id": "chatcmpl_02",
					"choices": [{
						"index": 0,
						"message": { "role": "assistant", "content": "done" },
						"finish_reason": "stop"
					}],
					"usage": {
						"prompt_tokens": 200,
						"completion_tokens": 50,
						"total_tokens": 250,
						"prompt_tokens_details": { "cached_tokens": 80 }
					}
				}
				""");
			return Task.FromResult(followUp);
		}, "test", NullLogger.Instance);

		var result = await orchestrator.OrchestrateAsync(context, CancellationToken.None);

		Assert.Equal("2", result.Headers.GetValues(CCRUsageMerger.HopCountHeader).Single());
		var finalBody = JsonNode.Parse(await result.Content.ReadAsStringAsync())!.AsObject();
		Assert.Equal(300, finalBody["usage"]!["prompt_tokens"]!.GetValue<long>());
		Assert.Equal(70, finalBody["usage"]!["completion_tokens"]!.GetValue<long>());
		Assert.Equal(370, finalBody["usage"]!["total_tokens"]!.GetValue<long>());
		Assert.Equal(120, finalBody["usage"]!["prompt_tokens_details"]!["cached_tokens"]!.GetValue<long>());
	}

	[Fact]
	public async Task SingleHop_LeavesResponseUntouched()
	{
		var (orchestrator, _) = BuildOrchestrator();
		using var request = Request();
		var initial = JsonResponse("""
			{
				"id": "chatcmpl_01",
				"choices": [{
					"index": 0,
					"message": { "role": "assistant", "content": "done" },
					"finish_reason": "stop"
				}],
				"usage": { "prompt_tokens": 42, "completion_tokens": 7, "total_tokens": 49 }
			}
			""");
		var context = new CCROrchestrationContext(request, initial, (_, _) => throw new InvalidOperationException("should not dispatch"), "test", NullLogger.Instance);

		var result = await orchestrator.OrchestrateAsync(context, CancellationToken.None);

		Assert.False(result.Headers.Contains(CCRUsageMerger.HopCountHeader));
		var finalBody = JsonNode.Parse(await result.Content.ReadAsStringAsync())!.AsObject();
		Assert.Equal(42, finalBody["usage"]!["prompt_tokens"]!.GetValue<long>());
	}

	[Fact]
	public async Task MalformedArguments_ReturnsEmptyArray()
	{
		// Chat Completions encodes arguments as a JSON-encoded string. A malformed string should
		// surface as "no retrieval key" — empty result, no crash.
		var (orchestrator, _) = BuildOrchestrator();

		string? capturedBody = null;

		var malformed = """
			{
				"id": "chatcmpl_01",
				"choices": [{
					"index": 0,
					"message": {
						"role": "assistant",
						"tool_calls": [{
							"id": "call_x",
							"type": "function",
							"function": { "name": "tophat_retrieve", "arguments": "not valid json" }
						}]
					},
					"finish_reason": "tool_calls"
				}]
			}
			""";

		using var request = Request();
		var initial = JsonResponse(malformed);
		var context = new CCROrchestrationContext(request, initial, async (req, _) =>
		{
			capturedBody = await req.Content!.ReadAsStringAsync();
			return JsonResponse(FinalAnswerResponse());
		}, "test", NullLogger.Instance);

		await orchestrator.OrchestrateAsync(context, CancellationToken.None);

		var body = JsonNode.Parse(capturedBody!)!.AsObject();
		var toolTurn = body["messages"]!.AsArray()[4]!.AsObject();
		var retrieved = JsonNode.Parse(toolTurn["content"]!.GetValue<string>())!.AsArray();

		Assert.Empty(retrieved);
	}
}
