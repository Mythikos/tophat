using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TopHat.Compression.CCR.DependencyInjection;
using TopHat.DependencyInjection;
using TopHat.Providers;
using TopHat.Relevance.BM25.DependencyInjection;
using TopHat.Tests.Support;
using TopHat.Transforms.JsonContext;
using Xunit;

namespace TopHat.Tests.Compression.CCR;

/// <summary>Exercises real caller turns across nonstreaming retrieval, streaming, and resumed retrieval using the returned assistant history.</summary>
public sealed class CCRSessionStreamingTests
{
	/// <summary>Each fresh request honors its own stream mode and retrieval allowance while retaining the caller's accumulated history.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task ConsecutiveCallerTurns_StreamingPreservesDataAndLaterRetrievalResumes(TopHatTarget target)
	{
		var payloads = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["records_initial"] = Payload(1),
			["records_stream"] = Payload(2),
			["records_resumed"] = Payload(3),
		};
		var callerHistory = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Summarize the records, then fetch the next batch." });
		AppendAssistant(callerHistory, target, Envelope(target, 0, "Initial records requested.", "records_initial", 0, 0));
		AppendResult(callerHistory, target, "records_initial", payloads["records_initial"]);
		var expectedHistories = new Dictionary<int, JsonArray>();
		var expectedRetrieved = new Dictionary<int, JsonObject>();
		var retrievalKeys = new HashSet<string>(StringComparer.Ordinal);
		var dispatches = new int[4];
		var responseForStreaming = Envelope(target, 2, "Streaming records received; fetch the final batch.", "records_resumed", 200, 20);
		var streamedBytes = StreamResponse(target, responseForStreaming);
		var (client, _, provider) = TransformHandlerFactory.Build(services =>
		{
			services.AddTopHatBm25Relevance();
			services.AddTopHatJsonContextCompressor();
			services.AddTopHatCCR(options => options.MaxIterations = 1);
		}, behavior: async (request, cancellationToken) =>
		{
			var turn = int.Parse(Assert.Single(request.Headers.GetValues("x-session-turn")), CultureInfo.InvariantCulture);
			dispatches[turn]++;
			Assert.InRange(dispatches[turn], 1, turn == 2 ? 1 : 2);
			var upstream = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
			var history = upstream[HistoryProperty(target)]!.AsArray();
			var expected = expectedHistories[turn];
			var callerDefinition = Assert.Single(upstream["tools"]!.AsArray().OfType<JsonObject>(), tool => ToolName(target, tool) == "get_records");
			Assert.True(JsonNode.DeepEquals(RequestBody(target, expected, streaming: turn == 2)["tools"]![0], callerDefinition));
			// The default two-message window leaves every older caller message byte-for-byte equivalent.
			for (var index = 0; index < expected.Count - 2; index++)
			{
				Assert.True(JsonNode.DeepEquals(expected[index], history[index]), $"Turn {turn} changed frozen caller history at index {index}.");
			}
			AssertAllPairs(target, history);
			if (target == TopHatTarget.OpenAIResponses)
			{
				Assert.False(upstream["store"]!.GetValue<bool>());
			}
			if (turn == 2)
			{
				Assert.True(upstream["stream"]!.GetValue<bool>());
				Assert.True(JsonNode.DeepEquals(expected, history));
				AssertNoPrivateTool(target, upstream);
				Assert.Equal(payloads["records_stream"], ResultText(target, history, "records_stream"));
				return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(streamedBytes, Encoding.UTF8, "text/event-stream") };
			}
			Assert.False(upstream["stream"]!.GetValue<bool>());
			Assert.Single(upstream["tools"]!.AsArray().OfType<JsonObject>(), tool => ToolName(target, tool) == "tophat_retrieve");
			if (dispatches[turn] == 1)
			{
				Assert.Equal(expected.Count, history.Count);
				AssertNoPrivateHistory(target, history);
				var callerId = turn == 1 ? "records_initial" : "records_resumed";
				var compressedText = ResultText(target, history, callerId);
				Assert.NotEqual(payloads[callerId], compressedText);
				var compressed = JsonNode.Parse(compressedText)!.AsArray();
				var metadata = Assert.Single(compressed.OfType<JsonObject>(), item => item.ContainsKey("_tophat_compression"));
				var key = metadata["_tophat_compression"]!["retrieval_key"]!.GetValue<string>();
				Assert.True(retrievalKeys.Add(key), "A later caller turn must issue an independent retrieval key.");
				var retainedIds = compressed.OfType<JsonObject>().Where(item => item["id"] is not null).Select(item => item["id"]!.GetValue<int>()).ToHashSet();
				var dropped = JsonNode.Parse(payloads[callerId])!.AsArray().OfType<JsonObject>().First(item => !retainedIds.Contains(item["id"]!.GetValue<int>()));
				expectedRetrieved[turn] = (JsonObject)dropped.DeepClone();
				return JsonResponse(PrivateResponse(target, key, dropped["id"]!.GetValue<int>(), turn * 100, turn * 10));
			}
			Assert.Equal(expected.Count + 2, history.Count);
			var restored = Assert.Single(JsonNode.Parse(ResultText(target, history, "private_session"))!.AsArray());
			Assert.True(JsonNode.DeepEquals(expectedRetrieved[turn], restored));
			return JsonResponse(Envelope(target, turn, turn == 1 ? "First retrieval finished; fetch another batch." : "Final retrieval finished.", turn == 1 ? "records_stream" : null, turn * 7, turn * 3));
		});

		using ((IDisposable)provider)
		using (client)
		{
			Assert.Equal(2, provider.GetRequiredService<IOptions<JsonContextCompressorOptions>>().Value.UnfrozenMessageCount);
			for (var turn = 1; turn <= 3; turn++)
			{
				AssertNoPrivateHistory(target, callerHistory);
				AssertAllPairs(target, callerHistory);
				expectedHistories[turn] = (JsonArray)callerHistory.DeepClone();
				var body = RequestBody(target, callerHistory, streaming: turn == 2);
				using (var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(target)) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") })
				{
					request.Headers.Add("x-session-turn", turn.ToString(CultureInfo.InvariantCulture));
					using (var response = await client.SendAsync(request))
					{
						Assert.True(response.IsSuccessStatusCode);
						var returned = await response.Content.ReadAsStringAsync();
						Assert.DoesNotContain("tophat_retrieve", returned, StringComparison.Ordinal);
						JsonObject assistant;
						if (turn == 2)
						{
							Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
							Assert.Equal(streamedBytes, returned);
							Assert.False(response.Headers.Contains("X-TopHat-CCR-Hops"));
							assistant = ReadStreamedResponse(target, returned);
							Assert.True(JsonNode.DeepEquals(AssistantContent(target, responseForStreaming), AssistantContent(target, assistant)));
							Assert.Equal(200, assistant["usage"]![InputUsage(target)]!.GetValue<int>());
							Assert.Equal(20, assistant["usage"]![OutputUsage(target)]!.GetValue<int>());
						}
						else
						{
							assistant = JsonNode.Parse(returned)!.AsObject();
							Assert.Equal("2", Assert.Single(response.Headers.GetValues("X-TopHat-CCR-Hops")));
							Assert.Equal(turn * 107, assistant["usage"]![InputUsage(target)]!.GetValue<int>());
							Assert.Equal(turn * 13, assistant["usage"]![OutputUsage(target)]!.GetValue<int>());
						}
						AppendAssistant(callerHistory, target, assistant);
						if (turn < 3)
						{
							var nextId = turn == 1 ? "records_stream" : "records_resumed";
							var actualCall = Assert.Single(Calls(target, callerHistory), call => CallId(target, call) == nextId);
							Assert.Equal("get_records", CallName(target, actualCall));
							AppendResult(callerHistory, target, CallId(target, actualCall), payloads[nextId]);
						}
					}
				}
				AssertNoPrivateHistory(target, callerHistory);
				AssertAllPairs(target, callerHistory);
			}
			Assert.Equal(new[] { 0, 2, 1, 2 }, dispatches);
			Assert.Equal(2, retrievalKeys.Count);
			foreach (var (identifier, payload) in payloads)
			{
				Assert.Equal(payload, ResultText(target, callerHistory, identifier));
			}
		}
	}

	private static string Payload(int batch) => JsonSerializer.Serialize(Enumerable.Range(1, 100).Select(index => new { id = batch * 1000 + index, batch, status = "processed", message = "record inserted successfully into database" }));

	private static JsonObject RequestBody(TopHatTarget target, JsonArray history, bool streaming)
	{
		var parameters = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["batch"] = new JsonObject { ["type"] = "integer" } } };
		var tool = target switch
		{
			TopHatTarget.AnthropicMessages => new JsonObject { ["name"] = "get_records", ["input_schema"] = parameters },
			TopHatTarget.OpenAIChatCompletions => new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = "get_records", ["parameters"] = parameters } },
			_ => new JsonObject { ["type"] = "function", ["name"] = "get_records", ["parameters"] = parameters },
		};
		var body = new JsonObject { ["model"] = "test-model", ["stream"] = streaming, ["tools"] = new JsonArray(tool), [HistoryProperty(target)] = history.DeepClone() };
		if (target == TopHatTarget.OpenAIResponses)
		{
			body["store"] = false;
		}
		if (target == TopHatTarget.AnthropicMessages)
		{
			body["max_tokens"] = 512;
		}
		return body;
	}

	private static JsonObject Envelope(TopHatTarget target, int turn, string text, string? callerId, int inputTokens, int outputTokens)
	{
		JsonObject response;
		if (target == TopHatTarget.AnthropicMessages)
		{
			var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });
			if (callerId is not null)
			{
				content.Add(Call(target, callerId, "get_records", new JsonObject { ["batch"] = turn + 1 }));
			}
			response = new JsonObject { ["id"] = "message_" + turn, ["type"] = "message", ["role"] = "assistant", ["content"] = content, ["stop_reason"] = callerId is null ? "end_turn" : "tool_use" };
		}
		else if (target == TopHatTarget.OpenAIChatCompletions)
		{
			var message = new JsonObject { ["role"] = "assistant", ["content"] = text };
			if (callerId is not null)
			{
				message["tool_calls"] = new JsonArray(Call(target, callerId, "get_records", new JsonObject { ["batch"] = turn + 1 }));
			}
			response = new JsonObject { ["id"] = "chat_" + turn, ["object"] = "chat.completion", ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = message, ["finish_reason"] = callerId is null ? "stop" : "tool_calls" }) };
		}
		else
		{
			var output = new JsonArray(new JsonObject { ["type"] = "message", ["id"] = "message_" + turn, ["role"] = "assistant", ["status"] = "completed", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text, ["annotations"] = new JsonArray() }) });
			if (callerId is not null)
			{
				output.Add(Call(target, callerId, "get_records", new JsonObject { ["batch"] = turn + 1 }));
			}
			response = new JsonObject { ["id"] = "response_" + turn, ["object"] = "response", ["status"] = "completed", ["output"] = output };
		}
		response["usage"] = new JsonObject { [InputUsage(target)] = inputTokens, [OutputUsage(target)] = outputTokens };
		return response;
	}

	private static JsonObject PrivateResponse(TopHatTarget target, string key, int identifier, int inputTokens, int outputTokens)
	{
		var call = Call(target, "private_session", "tophat_retrieve", new JsonObject { ["retrieval_key"] = key, ["ids"] = new JsonArray(identifier) });
		var response = target switch
		{
			TopHatTarget.AnthropicMessages => new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(call), ["stop_reason"] = "tool_use" },
			TopHatTarget.OpenAIChatCompletions => new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = new JsonObject { ["role"] = "assistant", ["tool_calls"] = new JsonArray(call) }, ["finish_reason"] = "tool_calls" }) },
			_ => new JsonObject { ["status"] = "completed", ["output"] = new JsonArray(call) },
		};
		response["usage"] = new JsonObject { [InputUsage(target)] = inputTokens, [OutputUsage(target)] = outputTokens };
		return response;
	}

	private static JsonObject Call(TopHatTarget target, string identifier, string name, JsonObject arguments) => target switch
	{
		TopHatTarget.AnthropicMessages => new JsonObject { ["type"] = "tool_use", ["id"] = identifier, ["name"] = name, ["input"] = arguments },
		TopHatTarget.OpenAIChatCompletions => new JsonObject { ["type"] = "function", ["id"] = identifier, ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments.ToJsonString() } },
		_ => new JsonObject { ["type"] = "function_call", ["id"] = "function_" + identifier, ["call_id"] = identifier, ["name"] = name, ["arguments"] = arguments.ToJsonString(), ["status"] = "completed" },
	};

	private static void AppendAssistant(JsonArray history, TopHatTarget target, JsonObject response)
	{
		if (target == TopHatTarget.AnthropicMessages)
		{
			history.Add(new JsonObject { ["role"] = "assistant", ["content"] = response["content"]!.DeepClone() });
		}
		else if (target == TopHatTarget.OpenAIChatCompletions)
		{
			history.Add(response["choices"]![0]!["message"]!.DeepClone());
		}
		else
		{
			foreach (var item in response["output"]!.AsArray())
			{
				history.Add(item!.DeepClone());
			}
		}
	}

	private static void AppendResult(JsonArray history, TopHatTarget target, string identifier, string payload) => history.Add(target switch
	{
		TopHatTarget.AnthropicMessages => new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = identifier, ["content"] = payload }) },
		TopHatTarget.OpenAIChatCompletions => new JsonObject { ["role"] = "tool", ["tool_call_id"] = identifier, ["content"] = payload },
		_ => new JsonObject { ["type"] = "function_call_output", ["call_id"] = identifier, ["output"] = payload },
	});

	private static IEnumerable<JsonObject> Calls(TopHatTarget target, JsonArray history) => target switch
	{
		TopHatTarget.AnthropicMessages => history.OfType<JsonObject>().Where(item => item["role"]?.GetValue<string>() == "assistant").SelectMany(item => (item["content"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()).Where(item => item["type"]?.GetValue<string>() == "tool_use"),
		TopHatTarget.OpenAIChatCompletions => history.OfType<JsonObject>().SelectMany(item => (item["tool_calls"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()),
		_ => history.OfType<JsonObject>().Where(item => item["type"]?.GetValue<string>() == "function_call"),
	};

	private static IEnumerable<JsonObject> Results(TopHatTarget target, JsonArray history) => target switch
	{
		TopHatTarget.AnthropicMessages => history.OfType<JsonObject>().SelectMany(item => (item["content"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()).Where(item => item["type"]?.GetValue<string>() == "tool_result"),
		TopHatTarget.OpenAIChatCompletions => history.OfType<JsonObject>().Where(item => item["role"]?.GetValue<string>() == "tool"),
		_ => history.OfType<JsonObject>().Where(item => item["type"]?.GetValue<string>() == "function_call_output"),
	};

	private static string ResultId(TopHatTarget target, JsonObject result) => result[target == TopHatTarget.AnthropicMessages ? "tool_use_id" : target == TopHatTarget.OpenAIChatCompletions ? "tool_call_id" : "call_id"]!.GetValue<string>();

	private static string ResultText(TopHatTarget target, JsonArray history, string identifier) => Assert.Single(Results(target, history), result => ResultId(target, result) == identifier)[target == TopHatTarget.OpenAIResponses ? "output" : "content"]!.GetValue<string>();

	private static void AssertAllPairs(TopHatTarget target, JsonArray history)
	{
		var calls = Calls(target, history).ToArray();
		var results = Results(target, history).ToArray();
		Assert.Equal(calls.Length, results.Length);
		Assert.Equal(calls.Length, calls.Select(call => CallId(target, call)).Distinct(StringComparer.Ordinal).Count());
		foreach (var call in calls)
		{
			var result = Assert.Single(results, item => ResultId(target, item) == CallId(target, call));
			JsonNode callOwner = call;
			JsonNode resultOwner = result;
			while (!ReferenceEquals(callOwner.Parent, history))
			{
				callOwner = callOwner.Parent!;
			}
			while (!ReferenceEquals(resultOwner.Parent, history))
			{
				resultOwner = resultOwner.Parent!;
			}
			Assert.Equal(history.IndexOf(callOwner) + 1, history.IndexOf(resultOwner));
		}
	}

	private static void AssertNoPrivateTool(TopHatTarget target, JsonObject request) => Assert.DoesNotContain(request["tools"]!.AsArray().OfType<JsonObject>(), tool => ToolName(target, tool) == "tophat_retrieve");

	private static void AssertNoPrivateHistory(TopHatTarget target, JsonArray history) => Assert.DoesNotContain(Calls(target, history), call => CallName(target, call) == "tophat_retrieve");

	private static string CallId(TopHatTarget target, JsonObject call) => call[target == TopHatTarget.OpenAIResponses ? "call_id" : "id"]!.GetValue<string>();

	private static string CallName(TopHatTarget target, JsonObject call) => (target == TopHatTarget.OpenAIChatCompletions ? call["function"] : call)!["name"]!.GetValue<string>();

	private static string? ToolName(TopHatTarget target, JsonObject tool) => (target == TopHatTarget.OpenAIChatCompletions ? tool["function"] : tool)?["name"]?.GetValue<string>();

	private static string StreamResponse(TopHatTarget target, JsonObject response)
	{
		if (target == TopHatTarget.AnthropicMessages)
		{
			var stream = new StringBuilder();
			stream.Append(Event("message_start", new JsonObject { ["type"] = "message_start", ["message"] = new JsonObject { ["id"] = "stream_message", ["type"] = "message", ["role"] = "assistant", ["content"] = new JsonArray(), ["usage"] = new JsonObject { ["input_tokens"] = 200, ["output_tokens"] = 0 } } }));
			var content = response["content"]!.AsArray();
			for (var index = 0; index < content.Count; index++)
			{
				var block = (JsonObject)content[index]!.DeepClone();
				var arguments = block["type"]!.GetValue<string>() == "tool_use" ? block["input"]!.ToJsonString() : null;
				if (arguments is not null)
				{
					block["input"] = new JsonObject();
				}
				stream.Append(Event("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = index, ["content_block"] = block }));
				if (arguments is not null)
				{
					stream.Append(Event("content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = index, ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = arguments } }));
				}
				stream.Append(Event("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = index }));
			}
			stream.Append(Event("message_delta", new JsonObject { ["type"] = "message_delta", ["delta"] = new JsonObject { ["stop_reason"] = "tool_use" }, ["usage"] = new JsonObject { ["output_tokens"] = 20 } }));
			stream.Append(Event("message_stop", new JsonObject { ["type"] = "message_stop" }));
			return stream.ToString();
		}
		if (target == TopHatTarget.OpenAIChatCompletions)
		{
			var delta = response["choices"]![0]!["message"]!.DeepClone().AsObject();
			delta["tool_calls"]![0]!["index"] = 0;
			var first = new JsonObject { ["id"] = "stream_chat", ["object"] = "chat.completion.chunk", ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = null }) };
			var last = new JsonObject { ["id"] = "stream_chat", ["object"] = "chat.completion.chunk", ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = new JsonObject(), ["finish_reason"] = "tool_calls" }), ["usage"] = response["usage"]!.DeepClone() };
			return "data: " + first.ToJsonString() + "\n\ndata: " + last.ToJsonString() + "\n\ndata: [DONE]\n\n";
		}
		return Event("response.completed", new JsonObject { ["type"] = "response.completed", ["sequence_number"] = 1, ["response"] = response.DeepClone() });
	}

	private static JsonObject ReadStreamedResponse(TopHatTarget target, string wire)
	{
		var events = wire.Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal) && line != "data: [DONE]").Select(line => JsonNode.Parse(line[6..])!.AsObject()).ToArray();
		if (target == TopHatTarget.OpenAIResponses)
		{
			return Assert.Single(events, item => item["type"]!.GetValue<string>() == "response.completed")["response"]!.DeepClone().AsObject();
		}
		if (target == TopHatTarget.OpenAIChatCompletions)
		{
			var message = events[0]["choices"]![0]!["delta"]!.DeepClone().AsObject();
			foreach (var call in message["tool_calls"]!.AsArray().OfType<JsonObject>())
			{
				call.Remove("index");
			}
			return new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = message, ["finish_reason"] = events[^1]["choices"]![0]!["finish_reason"]!.DeepClone() }), ["usage"] = events[^1]["usage"]!.DeepClone() };
		}
		var response = events[0]["message"]!.DeepClone().AsObject();
		foreach (var item in events.Skip(1))
		{
			switch (item["type"]!.GetValue<string>())
			{
				case "content_block_start":
					response["content"]!.AsArray().Add(item["content_block"]!.DeepClone());
					break;
				case "content_block_delta":
					response["content"]![item["index"]!.GetValue<int>()]!["input"] = JsonNode.Parse(item["delta"]!["partial_json"]!.GetValue<string>());
					break;
				case "message_delta":
					response["stop_reason"] = item["delta"]!["stop_reason"]!.DeepClone();
					response["usage"]!["output_tokens"] = item["usage"]!["output_tokens"]!.DeepClone();
					break;
			}
		}
		return response;
	}

	private static string Event(string name, JsonObject value) => "event: " + name + "\ndata: " + value.ToJsonString() + "\n\n";

	private static JsonNode AssistantContent(TopHatTarget target, JsonObject response) => target == TopHatTarget.AnthropicMessages ? response["content"]! : target == TopHatTarget.OpenAIChatCompletions ? response["choices"]![0]!["message"]! : response["output"]!;

	private static string HistoryProperty(TopHatTarget target) => target == TopHatTarget.OpenAIResponses ? "input" : "messages";

	private static string InputUsage(TopHatTarget target) => target == TopHatTarget.OpenAIChatCompletions ? "prompt_tokens" : "input_tokens";

	private static string OutputUsage(TopHatTarget target) => target == TopHatTarget.OpenAIChatCompletions ? "completion_tokens" : "output_tokens";

	private static string Endpoint(TopHatTarget target) => target == TopHatTarget.AnthropicMessages ? "https://api.anthropic.com/v1/messages" : target == TopHatTarget.OpenAIChatCompletions ? "https://api.openai.com/v1/chat/completions" : "https://api.openai.com/v1/responses";

	private static HttpResponseMessage JsonResponse(JsonObject body) => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}
