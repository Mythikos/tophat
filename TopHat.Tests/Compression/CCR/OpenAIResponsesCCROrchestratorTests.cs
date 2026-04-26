using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TopHat.Compression.CCR;
using Xunit;

namespace TopHat.Tests.Compression.CCR;

public sealed class OpenAIResponsesCCROrchestratorTests
{
	private const string RequestBody = """
		{
			"model": "gpt-4o",
			"input": [
				{ "role": "user", "content": "what broke?" },
				{ "type": "function_call", "call_id": "fc_logs", "name": "get_logs", "arguments": "{}" },
				{ "type": "function_call_output", "call_id": "fc_logs", "output": "[...compressed payload...]" }
			],
			"tools": [{ "type": "function", "name": "tophat_retrieve" }]
		}
		""";

	private static (OpenAIResponsesCCROrchestrator Orchestrator, InMemoryCompressionContextStore Store) BuildOrchestrator(CCROptions? opts = null)
	{
		var options = Options.Create(opts ?? new CCROptions());
		var store = new InMemoryCompressionContextStore(options);
		return (new OpenAIResponsesCCROrchestrator(store, options), store);
	}

	private static HttpRequestMessage Request(string body = RequestBody) => new(HttpMethod.Post, "https://api.openai.com/v1/responses")
	{
		Content = new StringContent(body, Encoding.UTF8, "application/json"),
	};

	private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
	{
		Content = new StringContent(body, Encoding.UTF8, "application/json"),
	};

	private static string ResponseWithRetrievalFunctionCall(string retrievalKey, string callId = "fc_retrieve_1", int[]? ids = null)
	{
		var argsJson = ids is null
			? $"{{\\\"retrieval_key\\\":\\\"{retrievalKey}\\\"}}"
			: $"{{\\\"retrieval_key\\\":\\\"{retrievalKey}\\\",\\\"ids\\\":[{string.Join(",", ids)}]}}";

		return $$"""
			{
				"id": "resp_01",
				"object": "response",
				"output": [
					{
						"type": "function_call",
						"call_id": "{{callId}}",
						"name": "tophat_retrieve",
						"arguments": "{{argsJson}}"
					}
				],
				"status": "in_progress"
			}
			""";
	}

	private static string FinalAnswerResponse(string text = "All clear.") =>
		$$"""
		{
			"id": "resp_02",
			"object": "response",
			"output": [
				{ "type": "message", "role": "assistant", "content": [{ "type": "output_text", "text": "{{text}}" }] }
			],
			"status": "completed"
		}
		""";

	[Fact]
	public async Task NoFunctionCall_ReturnsInitialResponseUnchanged()
	{
		var (orchestrator, _) = BuildOrchestrator();
		using var request = Request();
		var initial = JsonResponse(FinalAnswerResponse());
		var context = new CCROrchestrationContext(request, initial, (_, _) => throw new InvalidOperationException("should not dispatch"), "test", NullLogger.Instance);

		var result = await orchestrator.OrchestrateAsync(context, CancellationToken.None);

		Assert.Same(initial, result);
	}

	[Fact]
	public async Task RetrievalFunctionCall_FulfilsAndAppendsOutput()
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
		var initial = JsonResponse(ResponseWithRetrievalFunctionCall(key, ids: new[] { 7, 42 }));
		var context = new CCROrchestrationContext(request, initial, async (req, _) =>
		{
			capturedFollowUpBody = await req.Content!.ReadAsStringAsync();
			return JsonResponse(FinalAnswerResponse("Found tokens 7, 42."));
		}, "test", NullLogger.Instance);

		var result = await orchestrator.OrchestrateAsync(context, CancellationToken.None);

		var followUpBody = JsonNode.Parse(capturedFollowUpBody!)!.AsObject();
		var input = followUpBody["input"]!.AsArray();

		// Original 3 input items + function_call echo + function_call_output = 5
		Assert.Equal(5, input.Count);

		var echoedCall = input[3]!.AsObject();
		Assert.Equal("function_call", echoedCall["type"]!.GetValue<string>());
		Assert.Equal("fc_retrieve_1", echoedCall["call_id"]!.GetValue<string>());
		Assert.Equal("tophat_retrieve", echoedCall["name"]!.GetValue<string>());

		var output = input[4]!.AsObject();
		Assert.Equal("function_call_output", output["type"]!.GetValue<string>());
		Assert.Equal("fc_retrieve_1", output["call_id"]!.GetValue<string>());

		var retrieved = JsonNode.Parse(output["output"]!.GetValue<string>())!.AsArray();
		Assert.Equal(2, retrieved.Count);
		Assert.Equal(7, retrieved[0]!["id"]!.GetValue<int>());
		Assert.Equal(42, retrieved[1]!["id"]!.GetValue<int>());

		var finalBody = JsonNode.Parse(await result.Content.ReadAsStringAsync())!.AsObject();
		Assert.Equal("completed", finalBody["status"]!.GetValue<string>());
	}

	[Fact]
	public async Task ForeignFunctionCallAlongsideRetrieval_PassesThroughUnchanged()
	{
		var (orchestrator, store) = BuildOrchestrator();
		store.Store("k", new JsonNode[] { new JsonObject { ["id"] = 1 } });

		var mixed = """
			{
				"id": "resp_01",
				"output": [
					{ "type": "function_call", "call_id": "fc_1", "name": "tophat_retrieve", "arguments": "{\"retrieval_key\":\"k\"}" },
					{ "type": "function_call", "call_id": "fc_2", "name": "user_defined_tool", "arguments": "{}" }
				],
				"status": "in_progress"
			}
			""";

		using var request = Request();
		var initial = JsonResponse(mixed);
		var context = new CCROrchestrationContext(request, initial, (_, _) => throw new InvalidOperationException("should not dispatch"), "test", NullLogger.Instance);

		var result = await orchestrator.OrchestrateAsync(context, CancellationToken.None);

		Assert.Same(initial, result);
	}

	[Fact]
	public async Task StringInput_PassesThroughEvenWithRetrievalCall()
	{
		// String-form input has no array to splice into — orchestrator must pass through.
		var (orchestrator, store) = BuildOrchestrator();
		store.Store("k", new JsonNode[] { new JsonObject { ["id"] = 1 } });

		var stringInputBody = """
			{
				"model": "gpt-4o",
				"input": "what broke?",
				"tools": [{ "type": "function", "name": "tophat_retrieve" }]
			}
			""";

		using var request = Request(stringInputBody);
		var initial = JsonResponse(ResponseWithRetrievalFunctionCall("k"));
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
			Content = new StringContent("event: response.created\ndata: {}\n\n", Encoding.UTF8, "text/event-stream"),
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
		var initial = JsonResponse(ResponseWithRetrievalFunctionCall("k"));
		var context = new CCROrchestrationContext(request, initial, (_, _) =>
		{
			callCount++;
			return Task.FromResult(JsonResponse(ResponseWithRetrievalFunctionCall("k", callId: $"fc_retrieve_{callCount + 1}")));
		}, "test", NullLogger.Instance);

		var result = await orchestrator.OrchestrateAsync(context, CancellationToken.None);

		Assert.Equal(2, callCount);

		var finalBody = JsonNode.Parse(await result.Content.ReadAsStringAsync())!.AsObject();
		Assert.Equal("in_progress", finalBody["status"]!.GetValue<string>());
	}

	[Fact]
	public async Task MultiHop_RewritesUsageCumulativelyAndStampsHopHeader()
	{
		var (orchestrator, store) = BuildOrchestrator();
		store.Store("k", new JsonNode[] { new JsonObject { ["id"] = 1 } });

		using var request = Request();
		var initial = JsonResponse("""
			{
				"id": "resp_01",
				"output": [{
					"type": "function_call",
					"call_id": "fc_1",
					"name": "tophat_retrieve",
					"arguments": "{\"retrieval_key\":\"k\"}"
				}],
				"status": "in_progress",
				"usage": {
					"input_tokens": 100,
					"output_tokens": 20,
					"total_tokens": 120,
					"input_tokens_details": { "cached_tokens": 30 }
				}
			}
			""");
		var context = new CCROrchestrationContext(request, initial, (_, _) =>
		{
			var followUp = JsonResponse("""
				{
					"id": "resp_02",
					"output": [{ "type": "message", "role": "assistant", "content": [{ "type": "output_text", "text": "done" }] }],
					"status": "completed",
					"usage": {
						"input_tokens": 175,
						"output_tokens": 25,
						"total_tokens": 200,
						"input_tokens_details": { "cached_tokens": 50 }
					}
				}
				""");
			return Task.FromResult(followUp);
		}, "test", NullLogger.Instance);

		var result = await orchestrator.OrchestrateAsync(context, CancellationToken.None);

		Assert.Equal("2", result.Headers.GetValues(CCRUsageMerger.HopCountHeader).Single());
		var finalBody = JsonNode.Parse(await result.Content.ReadAsStringAsync())!.AsObject();
		Assert.Equal(275, finalBody["usage"]!["input_tokens"]!.GetValue<long>());
		Assert.Equal(45, finalBody["usage"]!["output_tokens"]!.GetValue<long>());
		Assert.Equal(320, finalBody["usage"]!["total_tokens"]!.GetValue<long>());
		Assert.Equal(80, finalBody["usage"]!["input_tokens_details"]!["cached_tokens"]!.GetValue<long>());
	}

	[Fact]
	public async Task SingleHop_LeavesResponseUntouched()
	{
		var (orchestrator, _) = BuildOrchestrator();
		using var request = Request();
		var initial = JsonResponse("""
			{
				"id": "resp_01",
				"output": [{ "type": "message", "role": "assistant", "content": [{ "type": "output_text", "text": "done" }] }],
				"status": "completed",
				"usage": { "input_tokens": 42, "output_tokens": 7, "total_tokens": 49 }
			}
			""");
		var context = new CCROrchestrationContext(request, initial, (_, _) => throw new InvalidOperationException("should not dispatch"), "test", NullLogger.Instance);

		var result = await orchestrator.OrchestrateAsync(context, CancellationToken.None);

		Assert.False(result.Headers.Contains(CCRUsageMerger.HopCountHeader));
		var finalBody = JsonNode.Parse(await result.Content.ReadAsStringAsync())!.AsObject();
		Assert.Equal(42, finalBody["usage"]!["input_tokens"]!.GetValue<long>());
	}

	[Fact]
	public async Task InvalidRetrievalKey_ReturnsEmptyArrayInOutput()
	{
		var (orchestrator, _) = BuildOrchestrator();

		string? capturedBody = null;

		using var request = Request();
		var initial = JsonResponse(ResponseWithRetrievalFunctionCall("nonexistent"));
		var context = new CCROrchestrationContext(request, initial, async (req, _) =>
		{
			capturedBody = await req.Content!.ReadAsStringAsync();
			return JsonResponse(FinalAnswerResponse());
		}, "test", NullLogger.Instance);

		await orchestrator.OrchestrateAsync(context, CancellationToken.None);

		var body = JsonNode.Parse(capturedBody!)!.AsObject();
		var output = body["input"]!.AsArray()[4]!.AsObject();
		var retrieved = JsonNode.Parse(output["output"]!.GetValue<string>())!.AsArray();

		Assert.Empty(retrieved);
	}
}
