using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TopHat.Compression.CCR;
using TopHat.Compression.CCR.DependencyInjection;
using TopHat.DependencyInjection;
using TopHat.Providers;
using TopHat.Relevance.BM25.DependencyInjection;
using TopHat.Tests.Support;
using TopHat.Transforms;
using Xunit;

namespace TopHat.Tests.Compression.CCR;

/// <summary>Checks retrieval eligibility at the HTTP boundary before any tool data can be lost.</summary>
public sealed class CCRRequestSafetyTests
{
	/// <summary>CCR cannot advertise retrieval or drop tool data on an SSE request.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task StreamingWithCCR_PreservesToolPayloadAndDoesNotWriteStore(TopHatTarget target)
	{
		var body = RequestBody(target);
		body["stream"] = true;
		var (actual, store) = await SendAsync(target, body);
		Assert.True(JsonNode.DeepEquals(body, actual));
		Assert.Equal(0, store.Writes);
	}

	/// <summary>Streaming compression without CCR retains its existing opt-in behavior.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task StreamingWithoutCCR_StillCompressesWithoutAdvertisingRetrieval(TopHatTarget target)
	{
		var body = RequestBody(target);
		body["stream"] = true;
		var (actual, store) = await SendAsync(target, body, registerCCR: false, registerStore: false);
		Assert.NotEqual(ToolPayload(body, target), ToolPayload(actual, target));
		Assert.Contains("_tophat_compression", ToolPayload(actual, target), StringComparison.Ordinal);
		Assert.DoesNotContain("retrieval_key", ToolPayload(actual, target), StringComparison.Ordinal);
		Assert.Null(actual["tools"]);
		Assert.Equal(0, store.Writes);
	}

	/// <summary>A context store alone cannot guarantee response interception.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task StoreWithoutOrchestrator_DoesNotDropDataOrInjectTool(TopHatTarget target)
	{
		var body = RequestBody(target);
		var (actual, store) = await SendAsync(target, body, registerCCR: false);
		Assert.True(JsonNode.DeepEquals(body, actual));
		Assert.Equal(0, store.Writes);
	}

	/// <summary>Caller-defined tools with the reserved name must never be intercepted.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task CallerNameCollision_PreservesPayloadToolAndResponse(TopHatTarget target)
	{
		var body = RequestBody(target);
		var definition = new JsonObject { ["name"] = "tophat_retrieve", ["description"] = "A caller-owned tool", [target == TopHatTarget.AnthropicMessages ? "input_schema" : "parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() } };
		if (target == TopHatTarget.OpenAIResponses)
		{
			definition["type"] = "function";
		}
		body["tools"] = target == TopHatTarget.OpenAIChatCompletions
			? new JsonArray(new JsonObject { ["type"] = "function", ["function"] = definition })
			: new JsonArray(definition);
		var callerResponse = CollisionResponse(target);
		var (actual, store) = await SendAsync(target, body, responseBody: callerResponse);
		Assert.True(JsonNode.DeepEquals(body, actual));
		Assert.Equal(0, store.Writes);
	}

	/// <summary>Current JSON is authoritative even if an earlier transform enables streaming.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task EarlierTransformEnablesStreaming_CompressorDoesNotUseStaleSnapshot(TopHatTarget target)
	{
		var body = RequestBody(target);
		var (actual, store) = await SendAsync(target, body, configure: services => services.AddTopHatRequestTransform<EnableStreamingTransform>(options => options.WithOrder(100)));
		body["stream"] = true;
		Assert.True(JsonNode.DeepEquals(body, actual));
		Assert.Equal(0, store.Writes);
	}

	/// <summary>A late change to streaming must not dispatch previously compressed, unretrievable data.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task LaterTransformEnablesStreaming_RestoresPayloadAndRemovesInjectedTool(TopHatTarget target)
	{
		var body = RequestBody(target);
		var (actual, _) = await SendAsync(target, body, configure: services => services.AddTopHatRequestTransform<EnableStreamingTransform>(options => options.WithOrder(300)));
		body["stream"] = true;
		Assert.True(JsonNode.DeepEquals(body, actual));
	}

	/// <summary>Supported non-streaming requests retain retrieval-backed compression.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task NonStreamingWithCCR_StillCompressesAndRegistersRetrieval(TopHatTarget target)
	{
		var body = RequestBody(target);
		var (actual, store) = await SendAsync(target, body);
		Assert.NotEqual(ToolPayload(body, target), ToolPayload(actual, target));
		Assert.Contains("retrieval_key", ToolPayload(actual, target), StringComparison.Ordinal);
		Assert.NotNull(actual["tools"]);
		Assert.True(store.Writes > 0);
	}

	/// <summary>Multiple candidate completions cannot share a single retrieval conversation.</summary>
	[Fact]
	public async Task ChatMultipleChoices_DoesNotCompressOrInjectTool()
	{
		var body = RequestBody(TopHatTarget.OpenAIChatCompletions);
		body["n"] = 2;
		var (actual, store) = await SendAsync(TopHatTarget.OpenAIChatCompletions, body);
		Assert.True(JsonNode.DeepEquals(body, actual));
		Assert.Equal(0, store.Writes);
	}

	/// <summary>Async and server-managed continuations need explicit support before CCR can hide tools.</summary>
	[Theory]
	[InlineData("background", "true")]
	[InlineData("previous_response_id", "\"resp_previous\"")]
	[InlineData("conversation", "\"conv_existing\"")]
	[InlineData("store", "true")]
	[InlineData("store", "null")]
	public async Task ResponsesUnsupportedContinuation_DoesNotCompressOrInjectTool(string property, string value)
	{
		var body = RequestBody(TopHatTarget.OpenAIResponses);
		body[property] = JsonNode.Parse(value);
		var (actual, store) = await SendAsync(TopHatTarget.OpenAIResponses, body);
		Assert.True(JsonNode.DeepEquals(body, actual));
		Assert.Equal(0, store.Writes);
	}

	/// <summary>A caller policy forbidding internal tool use must not leave data dependent on retrieval.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task ToolChoiceNone_PreservesCompletePayload(TopHatTarget target)
	{
		var body = RequestBody(target);
		body["tool_choice"] = target == TopHatTarget.AnthropicMessages ? new JsonObject { ["type"] = "none" } : JsonValue.Create("none");
		var (actual, store) = await SendAsync(target, body);
		Assert.True(JsonNode.DeepEquals(body, actual));
		Assert.Equal(0, store.Writes);
	}

	/// <summary>A later transform removing the retrieval declaration must restore the data it would retrieve.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task LaterTransformRemovesTool_RestoresOriginalPayload(TopHatTarget target)
	{
		var body = RequestBody(target);
		var (actual, _) = await SendAsync(target, body, configure: services => services.AddTopHatRequestTransform<RemoveToolsTransform>(options => options.WithOrder(300)));
		Assert.True(JsonNode.DeepEquals(body, actual));
	}

	/// <summary>Irreversible edits to compressed data fail locally instead of sending an unusable retrieval key.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task LaterTransformAltersCompressedDataAndEnablesStreaming_RejectsBeforeDispatch(TopHatTarget target)
	{
		var error = await Assert.ThrowsAsync<HttpRequestException>(() => SendAsync(target, RequestBody(target), configure: services => services.AddTopHatRequestTransform<AlterCompressedDataTransform>(options => options.WithOrder(300))));
		Assert.Contains("cannot be forwarded safely", error.Message, StringComparison.Ordinal);
	}

	/// <summary>Known-owned definitions retain interception when a later transform adds provider metadata.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task LaterToolDecoration_StillOrchestratesInternalCall(TopHatTarget target)
	{
		var calls = 0;
		var (client, _, services) = TransformHandlerFactory.Build(collection =>
		{
			collection.AddTopHatBm25Relevance();
			collection.AddTopHatJsonContextCompressor();
			collection.AddTopHatCCR();
			collection.AddTopHatRequestTransform<DecorateToolTransform>(options => options.WithOrder(300));
		}, behavior: (_, _) =>
		{
			calls++;
			Assert.True(calls <= 2);
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(calls == 1 ? CollisionResponse(target).ToJsonString() : "{}", Encoding.UTF8, "application/json") });
		});
		using ((IDisposable)services)
		using (client)
		using (var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(target)) { Content = new StringContent(RequestBody(target).ToJsonString(), Encoding.UTF8, "application/json") })
		using (var response = await client.SendAsync(request))
		{
			Assert.Equal(2, calls);
			var result = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
			Assert.DoesNotContain("tophat_retrieve", result.ToJsonString(), StringComparison.Ordinal);
		}
	}

	/// <summary>Withdrawing a legacy internal definition also withdraws a forced selection of it.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task StreamingWithLegacyInternalTool_RemovesDefinitionAndForcedChoice(TopHatTarget target)
	{
		var body = RequestBody(target);
		body["stream"] = true;
		body["tools"] = new JsonArray(target switch
		{
			TopHatTarget.AnthropicMessages => CCRToolDefinition.BuildAnthropicToolDefinition(),
			TopHatTarget.OpenAIChatCompletions => CCRToolDefinition.BuildOpenAIChatCompletionsToolDefinition(),
			_ => CCRToolDefinition.BuildOpenAIResponsesToolDefinition(),
		});
		body["tool_choice"] = target switch
		{
			TopHatTarget.AnthropicMessages => new JsonObject { ["type"] = "tool", ["name"] = "tophat_retrieve" },
			TopHatTarget.OpenAIChatCompletions => new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = "tophat_retrieve" } },
			_ => new JsonObject { ["type"] = "function", ["name"] = "tophat_retrieve" },
		};
		var (actual, store) = await SendAsync(target, body);
		Assert.Empty(actual["tools"]!.AsArray());
		Assert.Null(actual["tool_choice"]);
		Assert.Equal(ToolPayload(body, target), ToolPayload(actual, target));
		Assert.Equal(0, store.Writes);
	}

	/// <summary>Changing the internal tool semantics cannot silently transfer ownership to a caller tool.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task LaterToolDefinitionMutation_RejectsBeforeDispatch(TopHatTarget target)
	{
		var error = await Assert.ThrowsAsync<HttpRequestException>(() => SendAsync(target, RequestBody(target), configure: services => services.AddTopHatRequestTransform<ChangeToolMeaningTransform>(options => options.WithOrder(300))));
		Assert.Contains("tool definition", error.Message, StringComparison.Ordinal);
	}

	private static async Task<(JsonObject Body, CountingStore Store)> SendAsync(TopHatTarget target, JsonObject body, bool registerCCR = true, bool registerStore = true, Action<IServiceCollection>? configure = null, JsonObject? responseBody = null)
	{
		var store = new CountingStore();
		JsonObject? captured = null;
		var dispatches = 0;
		var expectedResponse = responseBody?.ToJsonString() ?? "{}";
		var (client, _, services) = TransformHandlerFactory.Build(collection =>
		{
			collection.AddTopHatBm25Relevance();
			collection.AddTopHatJsonContextCompressor();
			if (registerStore)
			{
				collection.AddSingleton<ICompressionContextStore>(store);
			}
			if (registerCCR)
			{
				collection.AddTopHatCCR();
			}
			configure?.Invoke(collection);
		}, behavior: async (request, cancellationToken) =>
		{
			dispatches++;
			Assert.Equal(1, dispatches);
			captured = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
			return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(expectedResponse, Encoding.UTF8, "application/json") };
		});
		using ((IDisposable)services)
		using (client)
		using (var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(target)) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") })
		using (var response = await client.SendAsync(request))
		{
			Assert.Equal(expectedResponse, await response.Content.ReadAsStringAsync());
			Assert.NotNull(captured);
			return (captured, store);
		}
	}

	private static string Endpoint(TopHatTarget target) => target switch
	{
		TopHatTarget.AnthropicMessages => "https://api.anthropic.com/v1/messages",
		TopHatTarget.OpenAIChatCompletions => "https://api.openai.com/v1/chat/completions",
		_ => "https://api.openai.com/v1/responses",
	};

	private static JsonObject RequestBody(TopHatTarget target)
	{
		var payload = System.Text.Json.JsonSerializer.Serialize(Enumerable.Range(1, 100).Select(index => new { id = index, status = "processed", message = "record inserted successfully into database" }));
		var history = target switch
		{
			TopHatTarget.AnthropicMessages => JsonNode.Parse("""[{"role":"user","content":"summarize logs"},{"role":"assistant","content":[{"type":"tool_use","id":"caller_1","name":"get_logs","input":{}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"caller_1","content":""}]}]""")!.AsArray(),
			TopHatTarget.OpenAIChatCompletions => JsonNode.Parse("""[{"role":"user","content":"summarize logs"},{"role":"assistant","tool_calls":[{"id":"caller_1","type":"function","function":{"name":"get_logs","arguments":"{}"}}]},{"role":"tool","tool_call_id":"caller_1","content":""}]""")!.AsArray(),
			_ => JsonNode.Parse("""[{"role":"user","content":"summarize logs"},{"type":"function_call","call_id":"caller_1","name":"get_logs","arguments":"{}"},{"type":"function_call_output","call_id":"caller_1","output":""}]""")!.AsArray(),
		};
		if (target == TopHatTarget.AnthropicMessages)
		{
			history[2]!["content"]![0]!["content"] = payload;
		}
		else
		{
			history[2]![target == TopHatTarget.OpenAIResponses ? "output" : "content"] = payload;
		}
		var body = new JsonObject { ["model"] = target == TopHatTarget.AnthropicMessages ? "claude-sonnet-4-5" : "gpt-4o", [target == TopHatTarget.OpenAIResponses ? "input" : "messages"] = history };
		if (target == TopHatTarget.OpenAIResponses)
		{
			body["store"] = false;
		}
		return body;
	}

	private static string ToolPayload(JsonObject body, TopHatTarget target) => target switch
	{
		TopHatTarget.AnthropicMessages => body["messages"]![2]!["content"]![0]!["content"]!.GetValue<string>(),
		TopHatTarget.OpenAIChatCompletions => body["messages"]![2]!["content"]!.GetValue<string>(),
		_ => body["input"]![2]!["output"]!.GetValue<string>(),
	};

	private static JsonObject CollisionResponse(TopHatTarget target) => JsonNode.Parse(target switch
	{
		TopHatTarget.AnthropicMessages => """{"content":[{"type":"tool_use","id":"custom_1","name":"tophat_retrieve","input":{"query":"caller operation"}}],"stop_reason":"tool_use"}""",
		TopHatTarget.OpenAIChatCompletions => """{"choices":[{"index":0,"message":{"role":"assistant","tool_calls":[{"id":"custom_1","type":"function","function":{"name":"tophat_retrieve","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}""",
		_ => """{"output":[{"type":"function_call","call_id":"custom_1","name":"tophat_retrieve","arguments":"{}"}]}""",
	})!.AsObject();

	private sealed class CountingStore : ICompressionContextStore
	{
		public int Writes { get; private set; }

		public void Store(string retrievalKey, IReadOnlyList<JsonNode> droppedItems) => this.Writes++;

		public IReadOnlyList<JsonNode> Retrieve(string retrievalKey, IReadOnlySet<int>? ids, int limit) => Array.Empty<JsonNode>();
	}

	/// <summary>Changes the request mode to exercise live-body eligibility checks.</summary>
	public sealed class EnableStreamingTransform : IRequestTransform
	{
		/// <inheritdoc/>
		public ValueTask InvokeAsync(RequestTransformContext context, CancellationToken cancellationToken)
		{
			context.Body!["stream"] = true;
			context.MarkMutated();
			return ValueTask.CompletedTask;
		}
	}

	/// <summary>Removes the synthetic declaration after the compressor has run.</summary>
	public sealed class RemoveToolsTransform : IRequestTransform
	{
		/// <inheritdoc/>
		public ValueTask InvokeAsync(RequestTransformContext context, CancellationToken cancellationToken)
		{
			context.Body!.AsObject().Remove("tools");
			context.MarkMutated();
			return ValueTask.CompletedTask;
		}
	}

	/// <summary>Simulates a third-party transform changing compressed data and switching request mode.</summary>
	public sealed class AlterCompressedDataTransform : IRequestTransform
	{
		/// <inheritdoc/>
		public ValueTask InvokeAsync(RequestTransformContext context, CancellationToken cancellationToken)
		{
			var body = context.Body!.AsObject();
			var payload = ToolPayload(body, context.Target) + " ";
			if (context.Target == TopHatTarget.AnthropicMessages)
			{
				body["messages"]![2]!["content"]![0]!["content"] = payload;
			}
			else
			{
				body[context.Target == TopHatTarget.OpenAIResponses ? "input" : "messages"]![2]![context.Target == TopHatTarget.OpenAIResponses ? "output" : "content"] = payload;
			}
			body["stream"] = true;
			context.MarkMutated();
			return ValueTask.CompletedTask;
		}
	}

	/// <summary>Adds provider metadata without changing the synthetic function's schema or purpose.</summary>
	public sealed class DecorateToolTransform : IRequestTransform
	{
		/// <inheritdoc/>
		public ValueTask InvokeAsync(RequestTransformContext context, CancellationToken cancellationToken)
		{
			var tool = context.Body!["tools"]![0]!.AsObject();
			if (context.Target == TopHatTarget.AnthropicMessages)
			{
				tool["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
			}
			else
			{
				(context.Target == TopHatTarget.OpenAIChatCompletions ? tool["function"]!.AsObject() : tool)["strict"] = false;
			}
			context.MarkMutated();
			return ValueTask.CompletedTask;
		}
	}

	/// <summary>Changes the injected definition into a semantically different tool.</summary>
	public sealed class ChangeToolMeaningTransform : IRequestTransform
	{
		/// <inheritdoc/>
		public ValueTask InvokeAsync(RequestTransformContext context, CancellationToken cancellationToken)
		{
			var tool = context.Body!["tools"]![0]!.AsObject();
			(context.Target == TopHatTarget.OpenAIChatCompletions ? tool["function"]!.AsObject() : tool)["description"] = "A different tool entirely";
			context.MarkMutated();
			return ValueTask.CompletedTask;
		}
	}
}
