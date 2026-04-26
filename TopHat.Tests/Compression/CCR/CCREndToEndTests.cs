using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TopHat.Compression.CCR;
using TopHat.Compression.CCR.DependencyInjection;
using TopHat.DependencyInjection;
using TopHat.Relevance.BM25.DependencyInjection;
using TopHat.Tests.Support;
using Xunit;

namespace TopHat.Tests.Compression.CCR;

/// <summary>
/// End-to-end validation of CCR: client sends a request containing a compressible tool_result;
/// TopHat compresses + injects the retrieval tool; mock upstream responds first with a
/// tophat_retrieve tool_use, then with a final answer after the orchestrator fulfils the
/// retrieval. Verifies the client sees only the final answer.
/// </summary>
public sealed class CCREndToEndTests
{
	// Uniform payload — every item has the same schema and content except `id`. Guarantees
	// item 42 is dropped by the compressor (no special keywords for ErrorKeywordDetector to
	// latch onto, no BM25 signal favoring it over others under a benign query).
	private static string BigJsonArray(int count = 100) =>
		System.Text.Json.JsonSerializer.Serialize(
			Enumerable.Range(1, count).Select(i =>
				new { id = i, status = "processed", message = "record inserted successfully into database" })
			.ToArray());

	private static HttpRequestMessage AnthropicRequest(string toolResultContent) =>
		new (HttpMethod.Post, "https://api.anthropic.com/v1/messages")
		{
			Content = new StringContent($$"""
				{
					"model": "claude-opus-4-5",
					"messages": [
						{ "role": "user", "content": "summarize the log activity" },
						{ "role": "assistant", "content": [{ "type": "tool_use", "id": "tu_1", "name": "get_logs", "input": {} }] },
						{ "role": "user", "content": [{ "type": "tool_result", "tool_use_id": "tu_1", "content": {{System.Text.Json.JsonSerializer.Serialize(toolResultContent)}} }] }
					]
				}
				""", Encoding.UTF8, "application/json"),
		};

	[Fact]
	public async Task FullLoop_ClientSeesOnlyTerminalAnswer()
	{
		// Track calls to the mock upstream so we can verify the orchestrator drove a second hop.
		var upstreamCalls = new List<JsonObject>();
		string? retrievalKeyFromFirstRequest = null;

		var (client, _, _) = TransformHandlerFactory.Build(
			services =>
			{
				services.AddTopHatBm25Relevance();
				services.AddTopHatJsonContextCompressor();
				services.AddTopHatCCR();
			},
			behavior: async (req, _) =>
			{
				var body = JsonNode.Parse(await req.Content!.ReadAsByteArrayAsync())!.AsObject();
				upstreamCalls.Add(body);

				if (upstreamCalls.Count == 1)
				{
					// First call: compressor has injected tophat_retrieve tool and a retrieval_key
					// in the compressed payload. Extract the retrieval_key and respond with a
					// tool_use for tophat_retrieve asking for item id=42.
					var messages = body["messages"]!.AsArray();
					var toolResult = messages[2]!["content"]!.AsArray()[0]!["content"]!.GetValue<string>();
					var parsed = JsonNode.Parse(toolResult)!.AsArray();
					var metadata = parsed
						.OfType<JsonObject>()
						.Select(o => o["_tophat_compression"] as JsonObject)
						.First(m => m is not null);
					retrievalKeyFromFirstRequest = metadata!["retrieval_key"]!.GetValue<string>();

					var retrievalResponse = $$"""
					{
						"id": "msg_01",
						"type": "message",
						"role": "assistant",
						"content": [
							{
								"type": "tool_use",
								"id": "toolu_01",
								"name": "tophat_retrieve",
								"input": { "retrieval_key": "{{retrievalKeyFromFirstRequest}}", "ids": [42] }
							}
						],
						"stop_reason": "tool_use"
					}
					""";
					return new HttpResponseMessage(HttpStatusCode.OK)
					{
						Content = new StringContent(retrievalResponse, Encoding.UTF8, "application/json"),
					};
				}

				// Second call: conversation now has the assistant's tool_use AND a user tool_result
				// with the retrieved items. Verify then respond with a natural-language answer.
				var followUpMessages = body["messages"]!.AsArray();
				Assert.Equal(5, followUpMessages.Count);
				var toolResultTurn = followUpMessages[4]!.AsObject();
				Assert.Equal("user", toolResultTurn["role"]!.GetValue<string>());
				var toolResultBlock = toolResultTurn["content"]!.AsArray()[0]!.AsObject();
				var retrievedItems = JsonNode.Parse(toolResultBlock["content"]!.GetValue<string>())!.AsArray();
				Assert.Single(retrievedItems);
				Assert.Equal(42, retrievedItems[0]!["id"]!.GetValue<int>());

				var finalAnswer = """
				{
					"id": "msg_02",
					"type": "message",
					"role": "assistant",
					"content": [{ "type": "text", "text": "Item 42 shows status 'error' with message 'disk full'." }],
					"stop_reason": "end_turn"
				}
				""";
				return new HttpResponseMessage(HttpStatusCode.OK)
				{
					Content = new StringContent(finalAnswer, Encoding.UTF8, "application/json"),
				};
			});

		var response = await client.SendAsync(AnthropicRequest(BigJsonArray()));
		var responseBody = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

		// Client sees only the terminal natural-language answer — retrieval round-trip is invisible.
		Assert.Equal("end_turn", responseBody["stop_reason"]!.GetValue<string>());
		Assert.Contains("Item 42", responseBody["content"]!.AsArray()[0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);

		// Upstream was called twice: once with the original compressed request, once with the
		// retrieval follow-up. The client issued a single SendAsync.
		Assert.Equal(2, upstreamCalls.Count);
		Assert.NotNull(retrievalKeyFromFirstRequest);
	}

	[Fact]
	public async Task FullLoop_ClientSeesSummedUsageAndHopHeader()
	{
		// E2E billing-accuracy check: when CCR fires N upstream hops, the client's response.usage
		// must reflect the sum (not the last hop), and X-TopHat-CCR-Hops must report N. This
		// drives the full TopHatHandler → orchestrator pipeline, not the orchestrator in isolation.
		string? retrievalKeyFromFirstRequest = null;
		var upstreamCount = 0;

		var (client, _, _) = TransformHandlerFactory.Build(
			services =>
			{
				services.AddTopHatBm25Relevance();
				services.AddTopHatJsonContextCompressor();
				services.AddTopHatCCR();
			},
			behavior: async (req, _) =>
			{
				upstreamCount++;
				var body = JsonNode.Parse(await req.Content!.ReadAsByteArrayAsync())!.AsObject();

				if (upstreamCount == 1)
				{
					var messages = body["messages"]!.AsArray();
					var toolResult = messages[2]!["content"]!.AsArray()[0]!["content"]!.GetValue<string>();
					var parsed = JsonNode.Parse(toolResult)!.AsArray();
					var metadata = parsed.OfType<JsonObject>()
						.Select(o => o["_tophat_compression"] as JsonObject)
						.First(m => m is not null);
					retrievalKeyFromFirstRequest = metadata!["retrieval_key"]!.GetValue<string>();

					var retrievalResponse = $$"""
					{
						"id": "msg_01",
						"type": "message",
						"role": "assistant",
						"content": [{
							"type": "tool_use",
							"id": "toolu_01",
							"name": "tophat_retrieve",
							"input": { "retrieval_key": "{{retrievalKeyFromFirstRequest}}", "ids": [42] }
						}],
						"stop_reason": "tool_use",
						"usage": { "input_tokens": 1200, "output_tokens": 80, "cache_read_input_tokens": 100 }
					}
					""";
					return new HttpResponseMessage(HttpStatusCode.OK)
					{
						Content = new StringContent(retrievalResponse, Encoding.UTF8, "application/json"),
					};
				}

				var finalAnswer = """
				{
					"id": "msg_02",
					"type": "message",
					"role": "assistant",
					"content": [{ "type": "text", "text": "Item 42 shows status 'error'." }],
					"stop_reason": "end_turn",
					"usage": { "input_tokens": 800, "output_tokens": 20, "cache_read_input_tokens": 50 }
				}
				""";
				return new HttpResponseMessage(HttpStatusCode.OK)
				{
					Content = new StringContent(finalAnswer, Encoding.UTF8, "application/json"),
				};
			});

		var response = await client.SendAsync(AnthropicRequest(BigJsonArray()));

		// Hop header reports the true upstream call count.
		Assert.Equal("2", response.Headers.GetValues(CCRUsageMerger.HopCountHeader).Single());

		// usage on the final response is cumulative, not just the last hop's 800/20/50.
		var responseBody = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
		Assert.Equal(2000, responseBody["usage"]!["input_tokens"]!.GetValue<long>());
		Assert.Equal(100, responseBody["usage"]!["output_tokens"]!.GetValue<long>());
		Assert.Equal(150, responseBody["usage"]!["cache_read_input_tokens"]!.GetValue<long>());

		Assert.Equal(2, upstreamCount);
	}

	[Fact]
	public async Task ModelDoesNotCallRetrieval_SingleHop()
	{
		// Control case: model answers directly without invoking the retrieval tool. Verifies CCR
		// adds no round-trips when the model is satisfied with the compressed payload.
		var upstreamCalls = 0;

		var (client, _, _) = TransformHandlerFactory.Build(
			services =>
			{
				services.AddTopHatBm25Relevance();
				services.AddTopHatJsonContextCompressor();
				services.AddTopHatCCR();
			},
			behavior: (_, _) =>
			{
				upstreamCalls++;
				var body = """
				{
					"id": "msg_direct",
					"type": "message",
					"role": "assistant",
					"content": [{ "type": "text", "text": "Answer derived from compressed payload." }],
					"stop_reason": "end_turn"
				}
				""";
				return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
				{
					Content = new StringContent(body, Encoding.UTF8, "application/json"),
				});
			});

		var response = await client.SendAsync(AnthropicRequest(BigJsonArray()));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal(1, upstreamCalls);
	}
}
