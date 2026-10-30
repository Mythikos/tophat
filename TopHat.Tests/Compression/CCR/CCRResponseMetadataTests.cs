using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TopHat.Compression.CCR;
using TopHat.Compression.CCR.DependencyInjection;
using TopHat.DependencyInjection;
using TopHat.Relevance.BM25.DependencyInjection;
using TopHat.Tests.Support;
using Xunit;

namespace TopHat.Tests.Compression.CCR;

/// <summary>Verifies private-tool removal from complete provider response envelopes.</summary>
public sealed class CCRResponseMetadataTests
{
	/// <summary>Provider-echoed tool definitions must hide the injected tool and preserve caller configuration.</summary>
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ResponsesToolEcho_DoesNotExposeInjectedDefinition(bool mixedToolCalls)
	{
		var callerDefinition = JsonNode.Parse("""{"type":"function","name":"lookup_next","description":"Caller-owned lookup tool","parameters":{"type":"object","properties":{"record":{"type":"string"}},"required":["record"]},"strict":false}""")!.AsObject();
		var requestBody = CreateRequest(callerDefinition);
		var upstreamCalls = 0;
		var advertisedInternalTool = false;
		JsonArray? expectedCallerOutput = null;
		var (client, _, provider) = TransformHandlerFactory.Build(services =>
		{
			services.AddTopHatBm25Relevance();
			services.AddTopHatJsonContextCompressor();
			services.AddTopHatCCR();
		}, behavior: async (request, cancellationToken) =>
		{
			upstreamCalls++;
			Assert.Equal(1, upstreamCalls);
			var upstream = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
			Assert.False(upstream["store"]!.GetValue<bool>());
			Assert.Equal("auto", upstream["tool_choice"]!.GetValue<string>());
			var tools = upstream["tools"]!.AsArray();
			Assert.Single(tools.OfType<JsonObject>(), tool => tool["name"]?.GetValue<string>() == "tophat_retrieve");
			Assert.True(JsonNode.DeepEquals(callerDefinition, Assert.Single(tools.OfType<JsonObject>(), tool => tool["name"]?.GetValue<string>() == "lookup_next")));
			advertisedInternalTool = true;

			var output = new JsonArray
			{
				new JsonObject
				{
					["type"] = "message",
					["id"] = "msg_final",
					["role"] = "assistant",
					["status"] = "completed",
					["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = "Records summarized.", ["annotations"] = new JsonArray() }),
				},
			};
			if (mixedToolCalls)
			{
				output.Add(new JsonObject { ["type"] = "function_call", ["id"] = "fc_caller", ["call_id"] = "caller_next", ["name"] = "lookup_next", ["arguments"] = "{\"record\":\"caller-selected\"}", ["status"] = "completed" });
			}
			expectedCallerOutput = (JsonArray)output.DeepClone();
			if (mixedToolCalls)
			{
				var compressed = JsonNode.Parse(upstream["input"]![2]!["output"]!.GetValue<string>())!.AsArray();
				var metadata = compressed.OfType<JsonObject>().Select(item => item["_tophat_compression"] as JsonObject).First(item => item is not null);
				var arguments = new JsonObject { ["retrieval_key"] = metadata!["retrieval_key"]!.GetValue<string>() };
				output.Insert(1, new JsonObject { ["type"] = "function_call", ["id"] = "fc_internal", ["call_id"] = "private_retrieval", ["name"] = "tophat_retrieve", ["arguments"] = arguments.ToJsonString(), ["status"] = "completed" });
			}

			// These are normal provider response fields, including when storage is disabled.
			var responseBody = new JsonObject
			{
				["id"] = "resp_final",
				["object"] = "response",
				["status"] = "completed",
				["store"] = false,
				["tools"] = tools.DeepClone(),
				["tool_choice"] = upstream["tool_choice"]!.DeepClone(),
				["output"] = output,
				["usage"] = new JsonObject { ["input_tokens"] = 100, ["output_tokens"] = 20, ["total_tokens"] = 120 },
			};
			return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseBody.ToJsonString(), Encoding.UTF8, "application/json") };
		});

		using ((IDisposable)provider)
		using (client)
		using (var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses") { Content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json") })
		using (var response = await client.SendAsync(request))
		{
			Assert.True(response.IsSuccessStatusCode);
			Assert.True(advertisedInternalTool);
			Assert.Equal(1, upstreamCalls);
			var actual = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
			Assert.True(JsonNode.DeepEquals(expectedCallerOutput, actual["output"]));
			Assert.Equal("auto", actual["tool_choice"]!.GetValue<string>());
			var returnedTools = actual["tools"]!.AsArray();
			Assert.DoesNotContain(returnedTools.OfType<JsonObject>(), tool => tool["name"]?.GetValue<string>() == "tophat_retrieve");
			Assert.True(JsonNode.DeepEquals(callerDefinition, Assert.Single(returnedTools)));
		}
	}

	/// <summary>Every terminal path removes owned metadata even when the provider normalizes the advertised definition.</summary>
	[Theory]
	[InlineData("final", false)]
	[InlineData("final", true)]
	[InlineData("mixed", false)]
	[InlineData("mixed", true)]
	[InlineData("post-hop", false)]
	[InlineData("post-hop", true)]
	[InlineData("budget", false)]
	[InlineData("budget", true)]
	public async Task TerminalEnvelope_HidesOwnedEchoAndPreservesCallerMetadata(string scenario, bool forcedSelection)
	{
		var callerTool = JsonNode.Parse("""{"type":"function","name":"lookup_next","description":"Keep tophat_retrieve in caller prose","parameters":{"type":"object","properties":{}},"strict":false,"caller_metadata":{"name":"tophat_retrieve"}}""")!.AsObject();
		var builtinTool = JsonNode.Parse("""{"type":"web_search_preview","search_context_size":"low"}""")!.AsObject();
		var privateTool = CCRToolDefinition.BuildOpenAIResponsesToolDefinition();
		var requestBody = new JsonObject
		{
			["model"] = "test-model",
			["store"] = false,
			["input"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Find records." }),
			["tools"] = new JsonArray(callerTool.DeepClone(), privateTool.DeepClone(), builtinTool.DeepClone()),
			["tool_choice"] = forcedSelection ? new JsonObject { ["type"] = "function", ["name"] = "tophat_retrieve" } : JsonValue.Create("auto"),
		};
		var echoedPrivateTool = privateTool.DeepClone().AsObject();
		echoedPrivateTool["strict"] = false;
		echoedPrivateTool["provider_metadata"] = "normalized definition";
		var callerOutput = new JsonArray(new JsonObject { ["type"] = "message", ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = "Answer about tophat_retrieve remains ordinary text.", ["annotations"] = new JsonArray() }) });
		if (scenario == "mixed")
		{
			callerOutput.Add(ResponseCall("foreign", "lookup_next"));
		}
		var final = new JsonObject
		{
			["id"] = "resp_final",
			["status"] = "completed",
			["tools"] = new JsonArray(callerTool.DeepClone(), echoedPrivateTool, builtinTool.DeepClone()),
			["tool_choice"] = requestBody["tool_choice"]!.DeepClone(),
			["metadata"] = new JsonObject { ["caller_note"] = "tophat_retrieve", ["tool_choice"] = "caller-owned text" },
			["output"] = callerOutput.DeepClone(),
		};
		if (scenario is "mixed" or "budget")
		{
			final["output"]!.AsArray().Insert(0, ResponseCall("private_final"));
		}
		var initialBody = scenario is "post-hop" or "budget" ? new JsonObject { ["output"] = new JsonArray(ResponseCall("private_initial")) } : final;
		var options = Options.Create(new CCROptions { MaxIterations = scenario == "budget" ? 0 : 1 });
		var orchestrator = new OpenAIResponsesCCROrchestrator(new InMemoryCompressionContextStore(options), options);
		var dispatches = 0;
		using (var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses") { Content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json") })
		using (var initial = JsonResponse(initialBody))
		{
			var context = new CCROrchestrationContext(request, initial, async (followUp, cancellationToken) =>
			{
				dispatches++;
				Assert.Equal(1, dispatches);
				var followUpBody = JsonNode.Parse(await followUp.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
				if (scenario == "budget")
				{
					Assert.DoesNotContain(followUpBody["tools"]!.AsArray().OfType<JsonObject>(), tool => tool["name"]?.GetValue<string>() == "tophat_retrieve");
					if (forcedSelection)
					{
						Assert.False(followUpBody.ContainsKey("tool_choice"));
					}
				}
				return JsonResponse(final);
			}, "metadata-regression", NullLogger.Instance);
			using (var response = await orchestrator.OrchestrateAsync(context, CancellationToken.None))
			{
				Assert.Equal(scenario is "post-hop" or "budget" ? 1 : 0, dispatches);
				var actual = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
				Assert.True(JsonNode.DeepEquals(callerOutput, actual["output"]));
				Assert.True(JsonNode.DeepEquals(final["metadata"], actual["metadata"]));
				Assert.True(JsonNode.DeepEquals(new JsonArray(callerTool.DeepClone(), builtinTool.DeepClone()), actual["tools"]));
				if (forcedSelection)
				{
					Assert.False(actual.ContainsKey("tool_choice"));
				}
				else
				{
					Assert.Equal("auto", actual["tool_choice"]!.GetValue<string>());
				}
			}
		}
	}

	/// <summary>The reserved function name does not confer ownership of caller definitions or custom tools.</summary>
	[Theory]
	[InlineData("function")]
	[InlineData("custom")]
	public async Task CallerOwnedSameNameTool_PreservesCompleteResponse(string toolType)
	{
		var definition = new JsonObject { ["type"] = toolType, ["name"] = "tophat_retrieve", ["description"] = "Caller-owned action", ["caller_data"] = new JsonObject { ["unchanged"] = true } };
		definition[toolType == "function" ? "parameters" : "format"] = toolType == "function" ? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() } : new JsonObject { ["type"] = "text" };
		var body = CreateRequest(definition);
		var responseBody = new JsonObject
		{
			["tools"] = new JsonArray(definition.DeepClone()),
			["tool_choice"] = new JsonObject { ["type"] = toolType, ["name"] = "tophat_retrieve" },
			["output"] = new JsonArray(new JsonObject { ["type"] = toolType == "function" ? "function_call" : "custom_tool_call", ["name"] = "tophat_retrieve", ["call_id"] = "caller_call", ["arguments"] = "{}", ["input"] = "caller text" }),
		};
		var originalResponse = responseBody.ToJsonString();
		var calls = 0;
		var (client, _, provider) = TransformHandlerFactory.Build(services =>
		{
			services.AddTopHatBm25Relevance();
			services.AddTopHatJsonContextCompressor();
			services.AddTopHatCCR();
		}, behavior: async (request, cancellationToken) =>
		{
			calls++;
			Assert.Equal(1, calls);
			Assert.True(JsonNode.DeepEquals(body, JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))));
			return JsonResponse(responseBody);
		});
		using ((IDisposable)provider)
		using (client)
		using (var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") })
		using (var response = await client.SendAsync(request))
		{
			Assert.Equal(1, calls);
			Assert.Equal(originalResponse, await response.Content.ReadAsStringAsync());
		}
	}

	/// <summary>Response metadata is not private merely because its name matches the reserved name.</summary>
	[Fact]
	public async Task NoAdvertisedPrivateTool_PreservesSameNameMetadata()
	{
		var options = Options.Create(new CCROptions());
		var body = new JsonObject { ["store"] = false, ["input"] = new JsonArray() };
		var responseBody = new JsonObject { ["output"] = new JsonArray(), ["tools"] = new JsonArray(CCRToolDefinition.BuildOpenAIResponsesToolDefinition()), ["tool_choice"] = new JsonObject { ["type"] = "function", ["name"] = "tophat_retrieve" } };
		var original = responseBody.ToJsonString();
		using (var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") })
		using (var initial = JsonResponse(responseBody))
		{
			var context = new CCROrchestrationContext(request, initial, (_, _) => throw new InvalidOperationException("A final answer must not dispatch."), "metadata-control", NullLogger.Instance);
			using (var response = await new OpenAIResponsesCCROrchestrator(new InMemoryCompressionContextStore(options), options).OrchestrateAsync(context, CancellationToken.None))
			{
				Assert.Equal(original, await response.Content.ReadAsStringAsync());
			}
		}
	}

	/// <summary>Proven ownership of a function does not confer ownership of a same-name custom tool or its selection.</summary>
	[Fact]
	public async Task OwnedDefinitionEcho_PreservesSameNameCustomToolAndSelection()
	{
		var customTool = JsonNode.Parse("""{"type":"custom","name":"tophat_retrieve","format":{"type":"text"},"description":"caller metadata"}""")!.AsObject();
		var customChoice = new JsonObject { ["type"] = "custom", ["name"] = "tophat_retrieve" };
		var customCall = new JsonObject { ["type"] = "custom_tool_call", ["call_id"] = "caller_custom", ["name"] = "tophat_retrieve", ["input"] = "preserved caller input" };
		var requestBody = new JsonObject { ["store"] = false, ["input"] = new JsonArray(), ["tools"] = new JsonArray(CCRToolDefinition.BuildOpenAIResponsesToolDefinition()) };
		var responseBody = new JsonObject
		{
			["output"] = new JsonArray(customCall.DeepClone()),
			["tools"] = new JsonArray(CCRToolDefinition.BuildOpenAIResponsesToolDefinition(), customTool.DeepClone()),
			["tool_choice"] = customChoice.DeepClone(),
		};
		var options = Options.Create(new CCROptions());
		using (var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses") { Content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json") })
		using (var initial = JsonResponse(responseBody))
		{
			var context = new CCROrchestrationContext(request, initial, (_, _) => throw new InvalidOperationException("Custom tools belong to the caller."), "custom-echo-control", NullLogger.Instance);
			using (var response = await new OpenAIResponsesCCROrchestrator(new InMemoryCompressionContextStore(options), options).OrchestrateAsync(context, CancellationToken.None))
			{
				var actual = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
				Assert.True(JsonNode.DeepEquals(customTool, Assert.Single(actual["tools"]!.AsArray())));
				Assert.True(JsonNode.DeepEquals(customChoice, actual["tool_choice"]));
				Assert.True(JsonNode.DeepEquals(customCall, Assert.Single(actual["output"]!.AsArray())));
			}
		}
	}

	/// <summary>Allowed-tool selections hide only an advertised private function and retain all caller selectors.</summary>
	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public async Task AllowedToolSelection_RemovesOnlyProvenPrivateFunction(bool advertisedPrivateTool, bool hasCallerSelectors)
	{
		var callerSelectors = hasCallerSelectors ? JsonNode.Parse("""[{"type":"function","name":"lookup_next"},{"type":"custom","name":"tophat_retrieve"},{"type":"mcp","server_label":"caller_server","name":"tophat_retrieve"}]""")!.AsArray() : new JsonArray();
		var selected = callerSelectors.DeepClone().AsArray();
		selected.Insert(0, new JsonObject { ["type"] = "function", ["name"] = "tophat_retrieve" });
		var selection = new JsonObject { ["type"] = "allowed_tools", ["mode"] = "required", ["tools"] = selected, ["caller_extension"] = "preserved" };
		var requestBody = new JsonObject { ["store"] = false, ["input"] = new JsonArray() };
		if (advertisedPrivateTool)
		{
			requestBody["tools"] = new JsonArray(CCRToolDefinition.BuildOpenAIResponsesToolDefinition());
		}
		var responseBody = new JsonObject { ["output"] = new JsonArray(), ["tool_choice"] = selection.DeepClone() };
		var original = responseBody.ToJsonString();
		var options = Options.Create(new CCROptions());
		using (var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses") { Content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json") })
		using (var initial = JsonResponse(responseBody))
		{
			var context = new CCROrchestrationContext(request, initial, (_, _) => throw new InvalidOperationException("A final envelope must not dispatch."), "allowed-choice-echo", NullLogger.Instance);
			using (var response = await new OpenAIResponsesCCROrchestrator(new InMemoryCompressionContextStore(options), options).OrchestrateAsync(context, CancellationToken.None))
			{
				var text = await response.Content.ReadAsStringAsync();
				var actual = JsonNode.Parse(text)!.AsObject();
				if (!advertisedPrivateTool)
				{
					Assert.Equal(original, text);
				}
				else if (hasCallerSelectors)
				{
					selection["tools"] = callerSelectors.DeepClone();
					Assert.True(JsonNode.DeepEquals(selection, actual["tool_choice"]));
				}
				else
				{
					Assert.False(actual.ContainsKey("tool_choice"));
				}
			}
		}
	}

	/// <summary>Withdrawing a replayed private definition also withdraws its selection while preserving caller selectors.</summary>
	[Theory]
	[InlineData("mixed")]
	[InlineData("private-only")]
	[InlineData("foreign-only")]
	[InlineData("forced-custom")]
	[InlineData("forced-mcp")]
	[InlineData("forced-function")]
	public async Task ReplayedDefinitionWithdrawal_PrunesOnlyPrivateSelections(string selectionKind)
	{
		var callerTool = JsonNode.Parse("""{"type":"function","name":"lookup_next","parameters":{"type":"object","properties":{}}}""")!.AsObject();
		var callerSelectors = JsonNode.Parse("""[{"type":"function","name":"lookup_next"},{"type":"custom","name":"tophat_retrieve"},{"type":"mcp","server_label":"caller_server","name":"tophat_retrieve"}]""")!.AsArray();
		var selection = new JsonObject { ["type"] = "allowed_tools", ["mode"] = "required", ["tools"] = callerSelectors.DeepClone(), ["caller_extension"] = "preserved" };
		var expectedSelection = selection.DeepClone();
		if (selectionKind == "mixed")
		{
			selection["tools"]!.AsArray().Insert(1, new JsonObject { ["type"] = "function", ["name"] = "tophat_retrieve" });
		}
		else if (selectionKind == "private-only")
		{
			selection["tools"] = new JsonArray(new JsonObject { ["type"] = "function", ["name"] = "tophat_retrieve" });
		}
		else if (selectionKind.StartsWith("forced-", StringComparison.Ordinal))
		{
			var kind = selectionKind["forced-".Length..];
			selection = new JsonObject { ["type"] = kind, ["name"] = kind == "function" ? "lookup_next" : "tophat_retrieve" };
			if (kind == "mcp")
			{
				selection["server_label"] = "caller_server";
			}
			expectedSelection = selection.DeepClone();
		}
		var body = CreateRequest(callerTool);
		body["tools"]!.AsArray().Add(CCRToolDefinition.BuildOpenAIResponsesToolDefinition());
		body["tool_choice"] = selection;
		var expected = CreateRequest(callerTool);
		if (selectionKind == "private-only")
		{
			expected.Remove("tool_choice");
		}
		else
		{
			expected["tool_choice"] = expectedSelection;
		}
		var dispatches = 0;
		var (client, _, provider) = TransformHandlerFactory.Build(services =>
		{
			services.AddTopHatBm25Relevance();
			services.AddTopHatJsonContextCompressor();
			services.AddTopHatCCR();
		}, behavior: async (request, cancellationToken) =>
		{
			dispatches++;
			Assert.Equal(1, dispatches);
			var actual = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
			Assert.True(JsonNode.DeepEquals(expected, actual), actual!.ToJsonString());
			return JsonResponse(new JsonObject { ["output"] = new JsonArray() });
		});
		using ((IDisposable)provider)
		using (client)
		using (var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") })
		using (var response = await client.SendAsync(request))
		{
			Assert.True(response.IsSuccessStatusCode);
			Assert.Equal(1, dispatches);
		}
	}

	/// <summary>Unsupported allowed-tool requests remain intact when no private definition establishes ownership.</summary>
	[Fact]
	public async Task AllowedToolRequestWithoutPrivateDefinition_RemainsUnchanged()
	{
		var body = CreateRequest(new JsonObject { ["type"] = "function", ["name"] = "lookup_next", ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() } });
		body["tool_choice"] = new JsonObject { ["type"] = "allowed_tools", ["mode"] = "auto", ["tools"] = new JsonArray(new JsonObject { ["type"] = "function", ["name"] = "tophat_retrieve" }) };
		var dispatches = 0;
		var (client, _, provider) = TransformHandlerFactory.Build(services =>
		{
			services.AddTopHatBm25Relevance();
			services.AddTopHatJsonContextCompressor();
			services.AddTopHatCCR();
		}, behavior: async (request, cancellationToken) =>
		{
			dispatches++;
			Assert.Equal(1, dispatches);
			Assert.True(JsonNode.DeepEquals(body, JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))));
			return JsonResponse(new JsonObject { ["output"] = new JsonArray() });
		});
		using ((IDisposable)provider)
		using (client)
		using (var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") })
		using (var response = await client.SendAsync(request))
		{
			Assert.True(response.IsSuccessStatusCode);
			Assert.Equal(1, dispatches);
		}
	}

	private static JsonObject ResponseCall(string id, string name = "tophat_retrieve") => new() { ["type"] = "function_call", ["call_id"] = id, ["name"] = name, ["arguments"] = "{\"retrieval_key\":\"missing-key\"}" };

	private static HttpResponseMessage JsonResponse(JsonObject body) => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

	private static JsonObject CreateRequest(JsonObject callerDefinition)
	{
		var payload = JsonSerializer.Serialize(Enumerable.Range(1, 100).Select(index => new { id = index, status = "processed", message = "record inserted successfully into database" }));
		return new JsonObject
		{
			["model"] = "gpt-4o",
			["store"] = false,
			["tool_choice"] = "auto",
			["tools"] = new JsonArray(callerDefinition.DeepClone()),
			["input"] = new JsonArray
			{
				new JsonObject { ["role"] = "user", ["content"] = "Summarize the records." },
				new JsonObject { ["type"] = "function_call", ["call_id"] = "original_lookup", ["name"] = "get_records", ["arguments"] = "{}" },
				new JsonObject { ["type"] = "function_call_output", ["call_id"] = "original_lookup", ["output"] = payload },
			},
		};
	}
}
