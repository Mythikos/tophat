using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TopHat.Compression.CCR.DependencyInjection;
using TopHat.DependencyInjection;
using TopHat.Providers;
using TopHat.Tests.Support;
using TopHat.Relevance.BM25.DependencyInjection;
using TopHat.Transforms;
using Xunit;

namespace TopHat.Tests.Compression.CCR;

/// <summary>Checks restoration of distinct tool-result slots when retrieval becomes unavailable.</summary>
public sealed class CCRRollbackTests
{
	/// <summary>Identical compressed text must not cause one caller result to overwrite another.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task EqualCompressedOutputs_RestoreEachDistinctOriginal(TopHatTarget target)
	{
		var body = CreateRequest(target, RepeatedStrings(100), RepeatedStrings(101));
		var observedCompression = false;
		var actual = await SendAsync(target, body, compressed =>
		{
			var payloads = Payloads(compressed, target);
			Assert.Equal(payloads["first"], payloads["second"]);
			Assert.NotEqual(Payloads(body, target)["first"], payloads["first"]);
			observedCompression = true;
		});

		Assert.True(observedCompression);
		Assert.True(JsonNode.DeepEquals(body, actual));
	}

	/// <summary>Reordering cloned messages must not change which original belongs to each call ID.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task ClonedReorderedResults_RestoreByCallerIdentity(TopHatTarget target)
	{
		var body = CreateRequest(target, RepeatedStrings(100), RepeatedStrings(101));
		var actual = await SendAsync(target, body, compressed =>
		{
			var payloads = Payloads(compressed, target);
			Assert.Equal(payloads["first"], payloads["second"]);
			CloneHistory(compressed, target);
			var results = ResultArray(compressed, target);
			var firstIndex = target == TopHatTarget.AnthropicMessages ? 0 : results.Count - 2;
			var first = results[firstIndex]!.DeepClone();
			results[firstIndex] = results[firstIndex + 1]!.DeepClone();
			results[firstIndex + 1] = first;
			compressed["stream"] = true;
		});

		Assert.Equal(Payloads(body, target)["first"], Payloads(actual, target)["first"]);
		Assert.Equal(Payloads(body, target)["second"], Payloads(actual, target)["second"]);
		Assert.True(actual["stream"]!.GetValue<bool>());
	}

	/// <summary>Removing retrieval metadata cannot hide an unresolved compression change.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task StrippedMetadataAfterCompression_RejectsBeforeDispatchWhenStreamingEnabled(TopHatTarget target)
	{
		var body = CreateRequest(target, DictionaryPayload(100), DictionaryPayload(101));
		await SendAsync(target, body, compressed =>
		{
			foreach (var result in Results(compressed, target))
			{
				var key = PayloadProperty(target);
				var array = JsonNode.Parse(result[key]!.GetValue<string>())!.AsArray();
				Assert.Contains(array, item => item is JsonObject entry && entry.ContainsKey("_tophat_compression"));
				for (var index = array.Count - 1; index >= 0; index--)
				{
					if (array[index] is JsonObject entry && entry.ContainsKey("_tophat_compression"))
					{
						array.RemoveAt(index);
					}
				}
				result[key] = array.ToJsonString();
			}
			compressed["stream"] = true;
		}, expectRejection: true);
	}

	/// <summary>Repeated compression after a clone restores the source before the first compression pass.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task RepeatedCompressionAfterClone_RestoresUltimateOriginal(TopHatTarget target)
	{
		var body = CreateRequest(target, DictionaryPayload(100), DictionaryPayload(101));
		Dictionary<string, string>? firstPass = null;
		var actual = await SendAsync(target, body, compressed =>
		{
			Assert.NotNull(firstPass);
			Assert.NotEqual(firstPass["first"], Payloads(compressed, target)["first"]);
			compressed["stream"] = true;
		}, repeatCompression: true, beforeSecondPass: compressed =>
		{
			firstPass = Payloads(compressed, target);
			CloneHistory(compressed, target);
		});

		Assert.Equal(Payloads(body, target)["first"], Payloads(actual, target)["first"]);
		Assert.Equal(Payloads(body, target)["second"], Payloads(actual, target)["second"]);
		Assert.Null(actual["tools"]);
	}

	/// <summary>Rollback must not silently erase caller edits made between compression passes.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task CallerEditBetweenCompressionPasses_RejectsAmbiguousRestoration(TopHatTarget target)
	{
		var body = CreateRequest(target, DictionaryPayload(100), DictionaryPayload(101));
		await SendAsync(target, body, compressed => compressed["stream"] = true, expectRejection: true, repeatCompression: true, beforeSecondPass: compressed =>
		{
			var first = Results(compressed, target).First();
			var key = PayloadProperty(target);
			var payload = JsonNode.Parse(first[key]!.GetValue<string>())!.AsArray();
			payload[0]!["message"] = "caller-controlled update after the first compression pass";
			first[key] = payload.ToJsonString();
		});
	}

	private static async Task<JsonObject> SendAsync(TopHatTarget target, JsonObject body, Action<JsonObject> mutate, bool expectRejection = false, bool repeatCompression = false, Action<JsonObject>? beforeSecondPass = null)
	{
		JsonObject? captured = null;
		var callbacks = new RollbackCallbacks(mutate, beforeSecondPass);
		var (client, inner, provider) = TransformHandlerFactory.Build(services =>
		{
			services.AddTopHatBm25Relevance();
			services.AddTopHatCCR();
			services.AddTopHatJsonContextCompressor(options =>
			{
				options.UnfrozenMessageCount = int.MaxValue;
				options.MinTokensToCrush = 1;
			});
			services.AddSingleton(callbacks);
			if (repeatCompression)
			{
				services.AddTopHatRequestTransform<BeforeSecondPassTransform>(options => options.WithOrder(220));
				services.AddTopHatJsonContextCompressor(configureRegistration: options => options.WithOrder(250));
			}
			services.AddTopHatRequestTransform<AfterCompressionTransform>(options => options.WithOrder(300));
		}, behavior: async (request, cancellationToken) =>
		{
			captured = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
			return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
		});

		using ((IDisposable)provider)
		using (client)
		using (var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(target)) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") })
		{
			if (expectRejection)
			{
				var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));
				Assert.Contains("CCR-compressed payload", error.Message, StringComparison.Ordinal);
				Assert.Empty(inner.ReceivedRequests);
				return body;
			}
			using (var response = await client.SendAsync(request))
			{
				Assert.True(response.IsSuccessStatusCode);
				Assert.Single(inner.ReceivedRequests);
				return Assert.IsType<JsonObject>(captured);
			}
		}
	}

	private static JsonObject CreateRequest(TopHatTarget target, string first, string second)
	{
		var transcript = target switch
		{
			TopHatTarget.AnthropicMessages => JsonNode.Parse("""[{"role":"user","content":"summarize records"},{"role":"assistant","content":[{"type":"tool_use","id":"first","name":"first_tool","input":{}},{"type":"tool_use","id":"second","name":"second_tool","input":{}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"first","content":""},{"type":"tool_result","tool_use_id":"second","content":""}]}]""")!.AsArray(),
			TopHatTarget.OpenAIChatCompletions => JsonNode.Parse("""[{"role":"user","content":"summarize records"},{"role":"assistant","tool_calls":[{"type":"function","id":"first","function":{"name":"first_tool","arguments":"{}"}},{"type":"function","id":"second","function":{"name":"second_tool","arguments":"{}"}}]},{"role":"tool","tool_call_id":"first","content":""},{"role":"tool","tool_call_id":"second","content":""}]""")!.AsArray(),
			_ => JsonNode.Parse("""[{"role":"user","content":"summarize records"},{"type":"function_call","call_id":"first","name":"first_tool","arguments":"{}"},{"type":"function_call","call_id":"second","name":"second_tool","arguments":"{}"},{"type":"function_call_output","call_id":"first","output":""},{"type":"function_call_output","call_id":"second","output":""}]""")!.AsArray(),
		};
		var body = new JsonObject { ["model"] = "test-model", [target == TopHatTarget.OpenAIResponses ? "input" : "messages"] = transcript };
		if (target == TopHatTarget.OpenAIResponses)
		{
			body["store"] = false;
		}
		var results = Results(body, target).ToArray();
		results[0][PayloadProperty(target)] = first;
		results[1][PayloadProperty(target)] = second;
		return body;
	}

	private static string RepeatedStrings(int count) => JsonSerializer.Serialize(Enumerable.Repeat("identical lengthy caller record with its original multiplicity preserved", count));
	private static string DictionaryPayload(int count) => JsonSerializer.Serialize(Enumerable.Range(0, count)
		.Select(index => new { id = index, message = $"{index} alpha{index * 31} beta{index * 127} gamma{index * 233}", status = "processed" }));
	private static string PayloadProperty(TopHatTarget target) => target == TopHatTarget.OpenAIResponses ? "output" : "content";
	private static string ResultIdProperty(TopHatTarget target) => target switch
	{
		TopHatTarget.AnthropicMessages => "tool_use_id",
		TopHatTarget.OpenAIChatCompletions => "tool_call_id",
		_ => "call_id",
	};
	private static IEnumerable<JsonObject> Results(JsonObject body, TopHatTarget target) => ResultArray(body, target).OfType<JsonObject>()
		.Where(item => item.ContainsKey(ResultIdProperty(target)) && item.ContainsKey(PayloadProperty(target)));
	private static JsonArray ResultArray(JsonObject body, TopHatTarget target) => target switch
	{
		TopHatTarget.AnthropicMessages => body["messages"]![2]!["content"]!.AsArray(),
		TopHatTarget.OpenAIChatCompletions => body["messages"]!.AsArray(),
		_ => body["input"]!.AsArray(),
	};
	private static Dictionary<string, string> Payloads(JsonObject body, TopHatTarget target) => Results(body, target)
		.ToDictionary(item => item[ResultIdProperty(target)]!.GetValue<string>(), item => item[PayloadProperty(target)]!.GetValue<string>(), StringComparer.Ordinal);
	private static void CloneHistory(JsonObject body, TopHatTarget target)
	{
		var property = target == TopHatTarget.OpenAIResponses ? "input" : "messages";
		body[property] = body[property]!.DeepClone();
	}
	private static string Endpoint(TopHatTarget target) => target switch
	{
		TopHatTarget.AnthropicMessages => "https://api.anthropic.com/v1/messages",
		TopHatTarget.OpenAIChatCompletions => "https://api.openai.com/v1/chat/completions",
		_ => "https://api.openai.com/v1/responses",
	};

	/// <summary>Per-test callbacks observed by the pipeline without shared global state.</summary>
	public sealed class RollbackCallbacks
	{
		/// <summary>Stores the final and optional intermediate mutation callbacks.</summary>
		public RollbackCallbacks(Action<JsonObject> afterCompression, Action<JsonObject>? beforeSecondPass)
		{
			this.AfterCompression = afterCompression;
			this.BeforeSecondPass = beforeSecondPass;
		}
		/// <summary>Runs after all compression passes.</summary>
		public Action<JsonObject> AfterCompression { get; }
		/// <summary>Runs between compression passes.</summary>
		public Action<JsonObject>? BeforeSecondPass { get; }
	}

	/// <summary>Exercises a transform that runs after compression.</summary>
	public sealed class AfterCompressionTransform : IRequestTransform
	{
		private readonly RollbackCallbacks _callbacks;
		/// <summary>Receives the callbacks for the current test.</summary>
		public AfterCompressionTransform(RollbackCallbacks callbacks) => this._callbacks = callbacks;
		/// <inheritdoc/>
		public ValueTask InvokeAsync(RequestTransformContext context, CancellationToken cancellationToken)
		{
			this._callbacks.AfterCompression(context.Body!.AsObject());
			context.MarkMutated();
			return ValueTask.CompletedTask;
		}
	}

	/// <summary>Exercises a clone between two compression passes.</summary>
	public sealed class BeforeSecondPassTransform : IRequestTransform
	{
		private readonly RollbackCallbacks _callbacks;
		/// <summary>Receives the callbacks for the current test.</summary>
		public BeforeSecondPassTransform(RollbackCallbacks callbacks) => this._callbacks = callbacks;
		/// <inheritdoc/>
		public ValueTask InvokeAsync(RequestTransformContext context, CancellationToken cancellationToken)
		{
			this._callbacks.BeforeSecondPass?.Invoke(context.Body!.AsObject());
			context.MarkMutated();
			return ValueTask.CompletedTask;
		}
	}
}
