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

/// <summary>Protects legacy Chat function calls without changing caller-owned function behavior.</summary>
public sealed class CCRLegacyChatTests
{
	/// <summary>Deprecated request settings cannot promise availability of the modern private tool.</summary>
	[Theory]
	[InlineData("functions", "[{\"name\":\"lookup_next\",\"parameters\":{\"type\":\"object\",\"properties\":{}}}]")]
	[InlineData("function_call", "\"none\"")]
	[InlineData("function_call", "\"auto\"")]
	[InlineData("function_call", "{\"name\":\"lookup_next\"}")]
	[InlineData("function_call", "{\"name\":\"tophat_retrieve\"}")]
	public async Task LegacyRequestSettings_DoNotCompressOrAdvertiseRetrieval(string property, string value)
	{
		var payload = JsonSerializer.Serialize(Enumerable.Range(1, 100).Select(index => new { id = index, status = "processed", message = "record inserted successfully into database" }));
		var body = new JsonObject
		{
			["model"] = "gpt-4o",
			[property] = JsonNode.Parse(value),
			["messages"] = new JsonArray
			{
				new JsonObject { ["role"] = "user", ["content"] = "Summarize the records." },
				new JsonObject { ["role"] = "assistant", ["tool_calls"] = new JsonArray(ModernCall("original", "get_records")) },
				new JsonObject { ["role"] = "tool", ["tool_call_id"] = "original", ["content"] = payload },
			},
		};
		var sent = 0;
		var (client, _, provider) = TransformHandlerFactory.Build(services =>
		{
			services.AddTopHatBm25Relevance();
			services.AddTopHatJsonContextCompressor();
			services.AddTopHatCCR();
		}, behavior: async (request, cancellationToken) =>
		{
			sent++;
			Assert.Equal(1, sent);
			var actual = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
			Assert.True(JsonNode.DeepEquals(body, actual), actual!.ToJsonString());
			return JsonResponse(ResponseBody());
		});
		using ((IDisposable)provider)
		using (client)
		using (var request = Request(body))
		using (var response = await client.SendAsync(request))
		{
			Assert.True(response.IsSuccessStatusCode);
			Assert.Equal(1, sent);
		}
	}

	/// <summary>A private legacy call has no pairable modern ID and must be hidden at the terminal boundary.</summary>
	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public async Task PrivateLegacyCall_IsRemovedWithoutDispatch(bool withCallerTool, bool afterRetrieval)
	{
		var caller = ModernCall("caller_action", "lookup_next");
		var finalBody = ResponseBody(LegacyCall("tophat_retrieve"), withCallerTool ? caller : null);
		var initialBody = afterRetrieval ? ResponseBody(modernCall: ModernCall("initial")) : finalBody;
		var dispatches = 0;
		using (var request = Request(ModernRequest()))
		using (var initial = JsonResponse(initialBody))
		{
			var context = new CCROrchestrationContext(request, initial, (_, _) =>
			{
				dispatches++;
				Assert.True(afterRetrieval && dispatches == 1, "An unpairable legacy call must not dispatch.");
				return Task.FromResult(JsonResponse(finalBody));
			}, "legacy-private", NullLogger.Instance);
			using (var response = await CreateOrchestrator().OrchestrateAsync(context, CancellationToken.None))
			{
				Assert.Equal(afterRetrieval ? 1 : 0, dispatches);
				var actual = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
				var choice = actual["choices"]![0]!.AsObject();
				var message = choice["message"]!.AsObject();
				Assert.False(message.ContainsKey("function_call"));
				Assert.Equal("Caller-visible text and tophat_retrieve prose.", message["content"]!.GetValue<string>());
				Assert.Equal("caller fingerprint", actual["system_fingerprint"]!.GetValue<string>());
				Assert.Equal(withCallerTool ? "tool_calls" : "stop", choice["finish_reason"]!.GetValue<string>());
				if (withCallerTool)
				{
					Assert.True(JsonNode.DeepEquals(caller, Assert.Single(message["tool_calls"]!.AsArray())));
				}
				else
				{
					Assert.False(message.ContainsKey("tool_calls"));
				}
			}
		}
	}

	/// <summary>An extra ID on a legacy response does not make it a valid modern call/result pair.</summary>
	[Fact]
	public async Task PrivateLegacyCallWithUnexpectedId_RemainsTerminal()
	{
		var legacy = LegacyCall("tophat_retrieve");
		legacy["id"] = "provider-added-id";
		using (var request = Request(ModernRequest()))
		using (var initial = JsonResponse(ResponseBody(legacy)))
		{
			var context = new CCROrchestrationContext(request, initial, (_, _) => throw new InvalidOperationException("Legacy IDs do not establish modern pairing."), "legacy-extra-id", NullLogger.Instance);
			using (var response = await CreateOrchestrator().OrchestrateAsync(context, CancellationToken.None))
			{
				var actual = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
				Assert.False(actual["choices"]![0]!["message"]!.AsObject().ContainsKey("function_call"));
				Assert.Equal("stop", actual["choices"]![0]!["finish_reason"]!.GetValue<string>());
			}
		}
	}

	/// <summary>A caller's legacy action must never be copied into a hidden modern retrieval continuation.</summary>
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ForeignLegacyCall_StopsMixedModernRetrieval(bool afterRetrieval)
	{
		var legacyCaller = LegacyCall("lookup_next");
		var finalBody = ResponseBody(legacyCaller, ModernCall("mixed_internal"));
		var initialBody = afterRetrieval ? ResponseBody(modernCall: ModernCall("initial")) : finalBody;
		var dispatches = 0;
		using (var request = Request(ModernRequest()))
		using (var initial = JsonResponse(initialBody))
		{
			var context = new CCROrchestrationContext(request, initial, (_, _) =>
			{
				dispatches++;
				Assert.True(afterRetrieval && dispatches == 1, "Caller legacy calls must return without a retrieval continuation.");
				return Task.FromResult(JsonResponse(finalBody));
			}, "legacy-foreign", NullLogger.Instance);
			using (var response = await CreateOrchestrator().OrchestrateAsync(context, CancellationToken.None))
			{
				Assert.Equal(afterRetrieval ? 1 : 0, dispatches);
				var actual = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
				var choice = actual["choices"]![0]!.AsObject();
				Assert.Equal("function_call", choice["finish_reason"]!.GetValue<string>());
				Assert.True(JsonNode.DeepEquals(legacyCaller, choice["message"]!["function_call"]));
				Assert.False(choice["message"]!.AsObject().ContainsKey("tool_calls"));
				Assert.Equal("Caller-visible text and tophat_retrieve prose.", choice["message"]!["content"]!.GetValue<string>());
			}
		}
	}

	/// <summary>Every response choice is sanitized independently while preserving caller legacy choices.</summary>
	[Fact]
	public async Task MultipleChoices_RemovesOnlyPrivateLegacyChoice()
	{
		var body = ResponseBody(LegacyCall("lookup_next"));
		var expectedForeignChoice = body["choices"]![0]!.DeepClone();
		var privateChoice = ResponseBody(LegacyCall("tophat_retrieve"))["choices"]![0]!.DeepClone();
		privateChoice["index"] = 1;
		body["choices"]!.AsArray().Add(privateChoice);
		using (var request = Request(ModernRequest()))
		using (var initial = JsonResponse(body))
		{
			var context = new CCROrchestrationContext(request, initial, (_, _) => throw new InvalidOperationException("Multiple choices must not dispatch."), "legacy-choices", NullLogger.Instance);
			using (var response = await CreateOrchestrator().OrchestrateAsync(context, CancellationToken.None))
			{
				var actual = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
				Assert.True(JsonNode.DeepEquals(expectedForeignChoice, actual["choices"]![0]));
				Assert.False(actual["choices"]![1]!["message"]!.AsObject().ContainsKey("function_call"));
				Assert.Equal("stop", actual["choices"]![1]!["finish_reason"]!.GetValue<string>());
			}
		}
	}

	/// <summary>Legacy declarations establish caller ownership even when a caller chooses the reserved name.</summary>
	[Fact]
	public async Task SameNameLegacyDeclaration_PreservesCallerResponse()
	{
		var body = new JsonObject
		{
			["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Use my function." }),
			["functions"] = new JsonArray(new JsonObject { ["name"] = "tophat_retrieve", ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() } }),
			["function_call"] = new JsonObject { ["name"] = "tophat_retrieve" },
		};
		var responseBody = ResponseBody(LegacyCall("tophat_retrieve"));
		var original = responseBody.ToJsonString();
		using (var request = Request(body))
		using (var initial = JsonResponse(responseBody))
		{
			var context = new CCROrchestrationContext(request, initial, (_, _) => throw new InvalidOperationException("Caller-owned legacy tools must not dispatch."), "legacy-collision", NullLogger.Instance);
			using (var response = await CreateOrchestrator().OrchestrateAsync(context, CancellationToken.None))
			{
				Assert.Equal(original, await response.Content.ReadAsStringAsync());
			}
		}
	}

	/// <summary>Null and foreign legacy fields remain unchanged when no private call must be removed.</summary>
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task OrdinaryLegacyField_RemainsByteUnchanged(bool nullField)
	{
		var body = ResponseBody(nullField ? null : LegacyCall("lookup_next"));
		if (nullField)
		{
			body["choices"]![0]!["message"]!["function_call"] = null;
		}
		var original = body.ToJsonString();
		using (var request = Request(ModernRequest()))
		using (var initial = JsonResponse(body))
		{
			var context = new CCROrchestrationContext(request, initial, (_, _) => throw new InvalidOperationException("Ordinary legacy fields must not dispatch."), "legacy-control", NullLogger.Instance);
			using (var response = await CreateOrchestrator().OrchestrateAsync(context, CancellationToken.None))
			{
				Assert.Equal(original, await response.Content.ReadAsStringAsync());
			}
		}
	}

	private static JsonObject ModernRequest() => new()
	{
		["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Find records." }),
		["tools"] = new JsonArray(CCRToolDefinition.BuildOpenAIChatCompletionsToolDefinition()),
	};

	private static JsonObject LegacyCall(string name) => new() { ["name"] = name, ["arguments"] = "{\"retrieval_key\":\"missing-key\"}", ["caller_extension"] = "preserve when foreign" };

	private static JsonObject ModernCall(string id, string name = "tophat_retrieve") => new() { ["id"] = id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["arguments"] = "{\"retrieval_key\":\"missing-key\"}" } };

	private static JsonObject ResponseBody(JsonObject? legacyCall = null, JsonObject? modernCall = null)
	{
		var message = new JsonObject { ["role"] = "assistant", ["content"] = "Caller-visible text and tophat_retrieve prose." };
		if (legacyCall is not null)
		{
			message["function_call"] = legacyCall.DeepClone();
		}
		if (modernCall is not null)
		{
			message["tool_calls"] = new JsonArray(modernCall.DeepClone());
		}
		return new JsonObject
		{
			["system_fingerprint"] = "caller fingerprint",
			["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = message, ["finish_reason"] = legacyCall is not null ? "function_call" : modernCall is not null ? "tool_calls" : "stop" }),
		};
	}

	private static OpenAIChatCompletionsCCROrchestrator CreateOrchestrator()
	{
		var options = Options.Create(new CCROptions { MaxIterations = 1 });
		return new OpenAIChatCompletionsCCROrchestrator(new InMemoryCompressionContextStore(options), options);
	}

	private static HttpRequestMessage Request(JsonObject body) => new(HttpMethod.Post, "https://api.openai.com/v1/chat/completions") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

	private static HttpResponseMessage JsonResponse(JsonObject body) => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}
