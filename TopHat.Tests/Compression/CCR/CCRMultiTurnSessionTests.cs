using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TopHat.Compression.CCR;
using TopHat.Compression.CCR.DependencyInjection;
using TopHat.DependencyInjection;
using TopHat.Providers;
using TopHat.Relevance.BM25.DependencyInjection;
using TopHat.Tests.Support;
using TopHat.Transforms.JsonContext;
using Xunit;

namespace TopHat.Tests.Compression.CCR;

/// <summary>Exercises complete caller-managed sessions across fresh requests sharing the real handler and retrieval store.</summary>
public sealed class CCRMultiTurnSessionTests
{
	/// <summary>Private retrieval stays within each caller turn, resets its budget, and never contaminates subsequent caller history.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task FourCallerTurns_PreserveHistoryAndResetRetrievalLifecycle(TopHatTarget target)
	{
		using (var session = new CallerSession(target))
		{
			var first = await session.SendTurnAsync(0, 1);
			session.AppendReturnedAssistant(first);
			session.ExecuteReturnedCallerTool(first, "caller_records_1", "get_records", "first-batch");

			var exhausted = await session.SendTurnAsync(1, 3);
			session.AppendReturnedAssistant(exhausted);
			session.ExecuteReturnedCallerTool(exhausted, "caller_records_2", "get_records2", "second-batch");

			var recovered = await session.SendTurnAsync(2, 2);
			session.AppendReturnedAssistant(recovered);
			session.AppendUser("Thank you. Give a brief closing answer without fetching more records.");

			var ordinary = await session.SendTurnAsync(3, 1);
			session.AppendReturnedAssistant(ordinary);
			session.AssertCompletedSession();
		}
	}

	private sealed class CallerSession : IDisposable
	{
		private readonly TopHatTarget _target;
		private readonly HttpClient _client;
		private readonly IServiceProvider _services;
		private readonly ICompressionContextStore _store;
		private readonly JsonArray _callerHistory = new();
		private readonly JsonArray _callerTools;
		private readonly Dictionary<string, string> _callerPayloads = new(StringComparer.Ordinal);
		private readonly Dictionary<int, JsonObject> _retrievalArguments = new();
		private readonly Dictionary<int, JsonObject> _requestedRecords = new();
		private readonly List<int> _dispatchCounts = new();
		private readonly List<HttpRequestMessage> _clientRequests = new();
		private int _turn = -1;
		private int _dispatchesInTurn;

		/// <summary>Creates a real DI handler and singleton retrieval store with a one-retrieval budget per caller request.</summary>
		public CallerSession(TopHatTarget target)
		{
			this._target = target;
			this._callerTools = new JsonArray(this.ToolDefinition("get_records"), this.ToolDefinition("get_records2"));
			var (client, _, services) = TransformHandlerFactory.Build(collection =>
			{
				collection.AddTopHatBm25Relevance();
				collection.AddTopHatJsonContextCompressor();
				collection.AddTopHatCCR(options => options.MaxIterations = 1);
			}, behavior: this.RespondAsync);
			this._client = client;
			this._services = services;
			this._store = services.GetRequiredService<ICompressionContextStore>();
			Assert.IsType<InMemoryCompressionContextStore>(this._store);
			Assert.Equal(2, services.GetRequiredService<IOptions<JsonContextCompressorOptions>>().Value.UnfrozenMessageCount);
			this.AppendUser("Read the first batch of records, then fetch a second batch and summarize both.");
		}

		/// <summary>Sends a fresh request from caller-owned history and returns the actual sanitized provider response.</summary>
		public async Task<JsonObject> SendTurnAsync(int turn, int expectedDispatches)
		{
			this._turn = turn;
			this._dispatchesInTurn = 0;
			Assert.Same(this._store, this._services.GetRequiredService<ICompressionContextStore>());
			this.AssertNoPrivateHistory(this._callerHistory);
			var callerSnapshot = this._callerHistory.ToJsonString();
			var body = new JsonObject
			{
				["model"] = this._target == TopHatTarget.AnthropicMessages ? "claude-sonnet-4-5" : "gpt-4o",
				["tools"] = this._callerTools.DeepClone(),
				["tool_choice"] = this._target == TopHatTarget.AnthropicMessages ? new JsonObject { ["type"] = "auto" } : JsonValue.Create("auto"),
				[this.HistoryProperty] = this._callerHistory.DeepClone(),
			};
			if (this._target == TopHatTarget.OpenAIResponses)
			{
				body["store"] = false;
			}
			else if (this._target == TopHatTarget.AnthropicMessages)
			{
				body["max_tokens"] = 1024;
			}
			var bodySnapshot = body.ToJsonString();
			using (var request = new HttpRequestMessage(HttpMethod.Post, this.Endpoint) { Content = new StringContent(bodySnapshot, Encoding.UTF8, "application/json") })
			{
				Assert.DoesNotContain(this._clientRequests, prior => ReferenceEquals(prior, request));
				this._clientRequests.Add(request);
				using (var response = await this._client.SendAsync(request))
				{
					Assert.True(response.IsSuccessStatusCode);
					Assert.Equal(expectedDispatches, this._dispatchesInTurn);
					Assert.Equal(callerSnapshot, this._callerHistory.ToJsonString());
					Assert.Equal(bodySnapshot, body.ToJsonString());
					this._dispatchCounts.Add(this._dispatchesInTurn);
					var actual = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
					this.AssertNoPrivateResponse(actual);
					var inputUsageField = this._target == TopHatTarget.OpenAIChatCompletions ? "prompt_tokens" : "input_tokens";
					var outputUsageField = this._target == TopHatTarget.OpenAIChatCompletions ? "completion_tokens" : "output_tokens";
					Assert.Equal(expectedDispatches * 100, actual["usage"]![inputUsageField]!.GetValue<int>());
					Assert.Equal(expectedDispatches * 20, actual["usage"]![outputUsageField]!.GetValue<int>());
					if (this._target != TopHatTarget.AnthropicMessages)
					{
						Assert.Equal(expectedDispatches * 120, actual["usage"]!["total_tokens"]!.GetValue<int>());
					}
					var callerCalls = this.Calls(this.AssistantHistory(actual));
					if (turn is 0 or 1)
					{
						Assert.Equal(turn == 0 ? "get_records" : "get_records2", Assert.Single(callerCalls).Name);
					}
					else
					{
						Assert.Empty(callerCalls);
					}
					Assert.Contains(turn switch { 0 => "Fetching the first batch.", 1 => "Fetching the second batch.", 2 => "Both batches are summarized.", _ => "Session complete." }, actual.ToJsonString(), StringComparison.Ordinal);
					if (expectedDispatches > 1)
					{
						Assert.Equal(expectedDispatches.ToString(System.Globalization.CultureInfo.InvariantCulture), Assert.Single(response.Headers.GetValues("X-TopHat-CCR-Hops")));
					}
					else
					{
						Assert.False(response.Headers.Contains("X-TopHat-CCR-Hops"));
					}
					return actual;
				}
			}
		}

		/// <summary>Copies only the assistant items actually delivered to the caller into caller-owned history.</summary>
		public void AppendReturnedAssistant(JsonObject response)
		{
			foreach (var item in this.AssistantHistory(response))
			{
				this._callerHistory.Add(item!.DeepClone());
			}
			this.AssertNoPrivateHistory(this._callerHistory);
		}

		/// <summary>Executes the actual returned caller call and appends its complete independently generated result.</summary>
		public void ExecuteReturnedCallerTool(JsonObject response, string expectedId, string expectedName, string batch)
		{
			var call = Assert.Single(this.Calls(this.AssistantHistory(response)));
			Assert.Equal(expectedId, call.Id);
			Assert.Equal(expectedName, call.Name);
			var arguments = this.CallArguments(call.Node);
			Assert.Equal(batch, arguments["batch"]!.GetValue<string>());
			var firstId = batch == "first-batch" ? 1001 : 2001;
			var records = new JsonArray(Enumerable.Range(firstId, 100).Select(id => (JsonNode)new JsonObject
			{
				["id"] = id,
				["batch"] = batch,
				["status"] = "processed",
				["message"] = "record inserted successfully into database",
			}).ToArray());
			var payload = records.ToJsonString();
			this._callerPayloads.Add(call.Id, payload);
			var result = this.Result(call.Id, payload);
			if (this._target == TopHatTarget.AnthropicMessages)
			{
				this._callerHistory.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(result, new JsonObject { ["type"] = "text", ["text"] = "These are the complete " + batch + " records." }) });
			}
			else
			{
				this._callerHistory.Add(result);
			}
			this.AssertCompletePairing(this._callerHistory);
		}

		/// <summary>Appends a real user message, moving both previous tool results outside the default live window.</summary>
		public void AppendUser(string text) => this._callerHistory.Add(new JsonObject { ["role"] = "user", ["content"] = text });

		/// <summary>Checks the completed caller session independently of the scripted upstream responses.</summary>
		public void AssertCompletedSession()
		{
			Assert.Equal(new[] { 1, 3, 2, 1 }, this._dispatchCounts);
			Assert.Equal(4, this._clientRequests.Count);
			Assert.Equal(2, this._retrievalArguments.Count);
			Assert.NotEqual(this._retrievalArguments[1]["retrieval_key"]!.GetValue<string>(), this._retrievalArguments[2]["retrieval_key"]!.GetValue<string>());
			this.AssertNoPrivateHistory(this._callerHistory);
			this.AssertCompletePairing(this._callerHistory);
			var calls = this.Calls(this._callerHistory);
			Assert.Equal(new[] { "get_records", "get_records2" }, calls.Select(call => call.Name));
			foreach (var result in this.Results(this._callerHistory))
			{
				Assert.Equal(this._callerPayloads[result.Id], result.Text);
			}
		}

		/// <inheritdoc/>
		public void Dispose()
		{
			this._client.Dispose();
			((IDisposable)this._services).Dispose();
		}

		private string HistoryProperty => this._target == TopHatTarget.OpenAIResponses ? "input" : "messages";

		private string Endpoint => this._target switch
		{
			TopHatTarget.AnthropicMessages => "https://api.anthropic.com/v1/messages",
			TopHatTarget.OpenAIChatCompletions => "https://api.openai.com/v1/chat/completions",
			_ => "https://api.openai.com/v1/responses",
		};

		private async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var hop = this._dispatchesInTurn++;
			Assert.True(hop < (this._turn switch { 1 => 3, 2 => 2, _ => 1 }), "The handler exceeded the scripted turn's bounded dispatch count.");
			var upstream = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
			var history = upstream[this.HistoryProperty]!.AsArray();
			this.AssertCompletePairing(history);
			this.AssertCallerPrefix(history);
			Assert.Equal(this.Calls(this._callerHistory).Count + hop, this.Calls(history).Count);
			if (hop == 0)
			{
				Assert.Equal(this._callerHistory.Count, history.Count);
				this.AssertNoPrivateHistory(history);
			}
			var expectsPrivateTool = this._turn is 1 or 2 && !(this._turn == 1 && hop == 2);
			var tools = upstream["tools"]!.AsArray();
			Assert.Equal(expectsPrivateTool ? 3 : 2, tools.Count);
			Assert.Equal(expectsPrivateTool ? 1 : 0, tools.OfType<JsonObject>().Count(tool => this.ToolName(tool) == "tophat_retrieve"));
			foreach (var callerTool in this._callerTools.OfType<JsonObject>())
			{
				Assert.True(JsonNode.DeepEquals(callerTool, Assert.Single(tools.OfType<JsonObject>(), tool => this.ToolName(tool) == this.ToolName(callerTool))));
			}
			if (this._target == TopHatTarget.OpenAIResponses)
			{
				Assert.False(upstream["store"]!.GetValue<bool>());
				Assert.False(upstream.ContainsKey("previous_response_id"));
			}

			JsonObject response;
			if (this._turn == 0)
			{
				response = this.Response(upstream, "Fetching the first batch.", this.Call("caller_records_1", "get_records", new JsonObject { ["batch"] = "first-batch" }));
			}
			else if (this._turn is 1 or 2 && hop == 0)
			{
				this.PrepareRetrieval(history, this._turn == 1 ? "caller_records_1" : "caller_records_2");
				// Reuse this internal ID in a later caller turn: it must not survive in caller history.
				response = this.Response(upstream, "Inspecting one omitted record.", this.Call("private_retrieval", "tophat_retrieve", this._retrievalArguments[this._turn]));
			}
			else if (this._turn == 1 && hop == 1)
			{
				this.AssertRetrievedRecord(history);
				response = this.Response(upstream, "Requesting another record.", this.Call("private_repeat", "tophat_retrieve", this._retrievalArguments[1]));
			}
			else if (this._turn == 1)
			{
				var exhausted = Assert.Single(this.Results(history), result => result.Id == "private_repeat");
				Assert.Contains("budget", exhausted.Text, StringComparison.OrdinalIgnoreCase);
				Assert.Contains("exhaust", exhausted.Text, StringComparison.OrdinalIgnoreCase);
				response = this.Response(upstream, "Fetching the second batch.", this.Call("private_final", "tophat_retrieve", this._retrievalArguments[1]), this.Call("caller_records_2", "get_records2", new JsonObject { ["batch"] = "second-batch" }));
			}
			else if (this._turn == 2)
			{
				this.AssertRetrievedRecord(history);
				response = this.Response(upstream, "Both batches are summarized.");
			}
			else
			{
				Assert.DoesNotContain("_tophat_compression", history.ToJsonString(), StringComparison.Ordinal);
				response = this.Response(upstream, "Session complete.");
			}
			return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json") };
		}

		private void PrepareRetrieval(JsonArray history, string resultId)
		{
			var compressed = JsonNode.Parse(Assert.Single(this.Results(history), result => result.Id == resultId).Text)!.AsArray();
			var original = JsonNode.Parse(this._callerPayloads[resultId])!.AsArray();
			Assert.True(compressed.Count < original.Count);
			var metadata = Assert.Single(compressed.OfType<JsonObject>(), item => item["_tophat_compression"] is JsonObject)["_tophat_compression"]!.AsObject();
			var key = metadata["retrieval_key"]!.GetValue<string>();
			Assert.False(string.IsNullOrWhiteSpace(key));
			var retainedIds = compressed.OfType<JsonObject>().Where(item => item["id"] is not null).Select(item => item["id"]!.GetValue<int>()).ToHashSet();
			var dropped = original.OfType<JsonObject>().First(item => !retainedIds.Contains(item["id"]!.GetValue<int>()));
			this._requestedRecords.Add(this._turn, dropped.DeepClone().AsObject());
			this._retrievalArguments.Add(this._turn, new JsonObject { ["retrieval_key"] = key, ["ids"] = new JsonArray(dropped["id"]!.DeepClone()), ["limit"] = 1 });
		}

		private void AssertRetrievedRecord(JsonArray history)
		{
			var result = Assert.Single(this.Results(history), item => item.Id == "private_retrieval");
			var retrieved = JsonNode.Parse(result.Text)!.AsArray();
			Assert.True(JsonNode.DeepEquals(this._requestedRecords[this._turn], Assert.Single(retrieved)), result.Text);
			Assert.Equal(this._turn == 1 ? "first-batch" : "second-batch", retrieved[0]!["batch"]!.GetValue<string>());
		}

		private void AssertCallerPrefix(JsonArray upstreamHistory)
		{
			Assert.True(upstreamHistory.Count >= this._callerHistory.Count);
			var callerPrefix = new JsonArray(upstreamHistory.Take(this._callerHistory.Count).Select(item => item!.DeepClone()).ToArray());
			if (this._turn is 1 or 2)
			{
				var id = this._turn == 1 ? "caller_records_1" : "caller_records_2";
				var changed = Assert.Single(this.Results(callerPrefix), result => result.Id == id);
				Assert.NotEqual(this._callerPayloads[id], changed.Text);
				changed.Node[this._target == TopHatTarget.OpenAIResponses ? "output" : "content"] = this._callerPayloads[id];
			}
			Assert.True(JsonNode.DeepEquals(this._callerHistory, callerPrefix), "Only the current live tool result may differ from caller history; frozen results, caller calls, and text must be preserved.");
		}

		private void AssertNoPrivateResponse(JsonObject response)
		{
			Assert.DoesNotContain(this.Calls(this.AssistantHistory(response)), call => call.Name == "tophat_retrieve");
			if (response["tools"] is JsonArray tools)
			{
				Assert.DoesNotContain(tools.OfType<JsonObject>(), tool => this.ToolName(tool) == "tophat_retrieve");
			}
			if (this._target == TopHatTarget.OpenAIResponses)
			{
				Assert.True(JsonNode.DeepEquals(this._callerTools, response["tools"]));
				Assert.Equal("auto", response["tool_choice"]!.GetValue<string>());
			}
		}

		private void AssertNoPrivateHistory(JsonArray history)
		{
			Assert.DoesNotContain(this.Calls(history), call => call.Name == "tophat_retrieve");
			Assert.DoesNotContain(this.Results(history), result => result.Id.StartsWith("private_", StringComparison.Ordinal));
		}

		private void AssertCompletePairing(JsonArray history)
		{
			var calls = this.Calls(history);
			var results = this.Results(history);
			Assert.Equal(calls.Count, calls.Select(call => call.Id).Distinct(StringComparer.Ordinal).Count());
			Assert.Equal(calls.Count, results.Count);
			foreach (var call in calls)
			{
				Assert.False(string.IsNullOrWhiteSpace(call.Id));
				var result = Assert.Single(results, item => item.Id == call.Id);
				Assert.Equal(call.Index + 1, result.Index);
			}
			foreach (var result in results)
			{
				Assert.Single(calls, call => call.Id == result.Id);
			}
		}

		private List<(JsonObject Node, string Id, string Name, int Index)> Calls(JsonArray history)
		{
			var calls = new List<(JsonObject Node, string Id, string Name, int Index)>();
			for (var index = 0; index < history.Count; index++)
			{
				var item = history[index]!.AsObject();
				if (this._target == TopHatTarget.OpenAIResponses)
				{
					if (item["type"]?.GetValue<string>() == "function_call")
					{
						calls.Add((item, item["call_id"]!.GetValue<string>(), item["name"]!.GetValue<string>(), index));
					}
					continue;
				}
				if (item["role"]?.GetValue<string>() != "assistant")
				{
					continue;
				}
				var content = item[this._target == TopHatTarget.AnthropicMessages ? "content" : "tool_calls"] as JsonArray;
				foreach (var call in content?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
				{
					if (call["type"]?.GetValue<string>() == (this._target == TopHatTarget.AnthropicMessages ? "tool_use" : "function"))
					{
						calls.Add((call, call["id"]!.GetValue<string>(), this._target == TopHatTarget.AnthropicMessages ? call["name"]!.GetValue<string>() : call["function"]!["name"]!.GetValue<string>(), index));
					}
				}
			}
			return calls;
		}

		private List<(JsonObject Node, string Id, string Text, int Index)> Results(JsonArray history)
		{
			var results = new List<(JsonObject Node, string Id, string Text, int Index)>();
			for (var index = 0; index < history.Count; index++)
			{
				var item = history[index]!.AsObject();
				if (this._target == TopHatTarget.AnthropicMessages && item["role"]?.GetValue<string>() == "user" && item["content"] is JsonArray content)
				{
					foreach (var block in content.OfType<JsonObject>().Where(block => block["type"]?.GetValue<string>() == "tool_result"))
					{
						results.Add((block, block["tool_use_id"]!.GetValue<string>(), block["content"]!.GetValue<string>(), index));
					}
				}
				else if (this._target == TopHatTarget.OpenAIChatCompletions && item["role"]?.GetValue<string>() == "tool")
				{
					results.Add((item, item["tool_call_id"]!.GetValue<string>(), item["content"]!.GetValue<string>(), index));
				}
				else if (this._target == TopHatTarget.OpenAIResponses && item["type"]?.GetValue<string>() == "function_call_output")
				{
					results.Add((item, item["call_id"]!.GetValue<string>(), item["output"]!.GetValue<string>(), index));
				}
			}
			return results;
		}

		private JsonArray AssistantHistory(JsonObject response) => this._target switch
		{
			TopHatTarget.AnthropicMessages => new JsonArray(new JsonObject { ["role"] = "assistant", ["content"] = response["content"]!.DeepClone() }),
			TopHatTarget.OpenAIChatCompletions => new JsonArray(response["choices"]![0]!["message"]!.DeepClone()),
			_ => response["output"]!.DeepClone().AsArray(),
		};

		private JsonObject CallArguments(JsonObject call) => this._target == TopHatTarget.AnthropicMessages ? call["input"]!.AsObject() : JsonNode.Parse((this._target == TopHatTarget.OpenAIChatCompletions ? call["function"]!["arguments"] : call["arguments"])!.GetValue<string>())!.AsObject();

		private string? ToolName(JsonObject tool) => (this._target == TopHatTarget.OpenAIChatCompletions ? tool["function"]?["name"] : tool["name"])?.GetValue<string>();

		private JsonObject ToolDefinition(string name)
		{
			var parameters = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["batch"] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray("batch") };
			return this._target switch
			{
				TopHatTarget.AnthropicMessages => new JsonObject { ["name"] = name, ["description"] = "Fetch caller-owned records.", ["input_schema"] = parameters },
				TopHatTarget.OpenAIChatCompletions => new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["description"] = "Fetch caller-owned records.", ["parameters"] = parameters } },
				_ => new JsonObject { ["type"] = "function", ["name"] = name, ["description"] = "Fetch caller-owned records.", ["parameters"] = parameters },
			};
		}

		private JsonObject Call(string id, string name, JsonObject arguments) => this._target switch
		{
			TopHatTarget.AnthropicMessages => new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = arguments.DeepClone() },
			TopHatTarget.OpenAIChatCompletions => new JsonObject { ["id"] = id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments.ToJsonString() } },
			_ => new JsonObject { ["type"] = "function_call", ["id"] = "fc_" + id, ["call_id"] = id, ["name"] = name, ["arguments"] = arguments.ToJsonString(), ["status"] = "completed" },
		};

		private JsonObject Result(string id, string payload) => this._target switch
		{
			TopHatTarget.AnthropicMessages => new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = payload },
			TopHatTarget.OpenAIChatCompletions => new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = payload },
			_ => new JsonObject { ["type"] = "function_call_output", ["call_id"] = id, ["output"] = payload },
		};

		private JsonObject Response(JsonObject upstream, string text, params JsonObject[] calls)
		{
			var response = new JsonObject { ["id"] = $"response_{this._turn}_{this._dispatchesInTurn}", ["model"] = upstream["model"]!.DeepClone() };
			var output = new JsonArray(calls.Select(call => call.DeepClone()).ToArray());
			if (this._target == TopHatTarget.AnthropicMessages)
			{
				output.Insert(0, new JsonObject { ["type"] = "text", ["text"] = text });
				response["type"] = "message";
				response["role"] = "assistant";
				response["content"] = output;
				response["stop_reason"] = calls.Length > 0 ? "tool_use" : "end_turn";
				response["usage"] = new JsonObject { ["input_tokens"] = 100, ["output_tokens"] = 20 };
			}
			else if (this._target == TopHatTarget.OpenAIChatCompletions)
			{
				var message = new JsonObject { ["role"] = "assistant", ["content"] = text };
				if (calls.Length > 0)
				{
					message["tool_calls"] = output;
				}
				response["object"] = "chat.completion";
				response["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = message, ["finish_reason"] = calls.Length > 0 ? "tool_calls" : "stop" });
				response["usage"] = new JsonObject { ["prompt_tokens"] = 100, ["completion_tokens"] = 20, ["total_tokens"] = 120 };
			}
			else
			{
				output.Insert(0, new JsonObject { ["type"] = "message", ["id"] = $"msg_{this._turn}_{this._dispatchesInTurn}", ["role"] = "assistant", ["status"] = "completed", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text, ["annotations"] = new JsonArray() }) });
				response["object"] = "response";
				response["status"] = "completed";
				response["output"] = output;
				response["store"] = false;
				response["tools"] = upstream["tools"]!.DeepClone();
				response["tool_choice"] = upstream["tool_choice"]!.DeepClone();
				response["usage"] = new JsonObject { ["input_tokens"] = 100, ["output_tokens"] = 20, ["total_tokens"] = 120 };
			}
			return response;
		}
	}
}
