using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TopHat.Compression.CCR;
using TopHat.Compression.CCR.DependencyInjection;
using TopHat.Providers;
using TopHat.Tests.Support;
using Xunit;

namespace TopHat.Tests.Compression.CCR;

/// <summary>Audits replayed CCR artifacts when both providers use the same gateway host. No compressor or fresh tool injection is registered.</summary>
public sealed class CCRGatewayRoutingTests
{
	/// <summary>Changing the destination host must not bypass repair of an unmatched internal call from prior history.</summary>
	[Theory]
	[InlineData(TopHatTarget.OpenAIChatCompletions, true)]
	[InlineData(TopHatTarget.OpenAIChatCompletions, false)]
	[InlineData(TopHatTarget.OpenAIResponses, true)]
	[InlineData(TopHatTarget.OpenAIResponses, false)]
	public async Task ReplayedUnmatchedHistory_IsRepairedBeforeGatewayDispatch(TopHatTarget target, bool sharedGatewayHost)
	{
		JsonObject? upstream = null;
		var original = ReplayedRequest(target, includeResult: false);
		var (client, inner, provider) = TransformHandlerFactory.Build(services => services.AddTopHatCCR(), configureOptions: options =>
		{
			options.OpenAiBaseUrl = new Uri("https://openai.gateway.test");
			options.AnthropicBaseUrl = new Uri(sharedGatewayHost ? "https://openai.gateway.test" : "https://anthropic.gateway.test");
		}, behavior: async (request, cancellationToken) =>
		{
			Assert.Equal("openai.gateway.test", request.RequestUri!.Host);
			Assert.Equal(Path(target), request.RequestUri.AbsolutePath);
			upstream = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
			return Response(new JsonObject());
		});
		using ((IDisposable)provider)
		using (client)
		using (var request = Request(target, original))
		using (var response = await client.SendAsync(request))
		{
			Assert.True(response.IsSuccessStatusCode);
			Assert.Single(inner.ReceivedRequests);
			Assert.NotNull(upstream);
			Assert.True(JsonNode.DeepEquals(original["tools"], upstream["tools"]));
			var history = upstream[target == TopHatTarget.OpenAIResponses ? "input" : "messages"]!.AsArray();
			Assert.Equal("Preserve caller input.", history[0]!["content"]!.GetValue<string>());
			var result = Assert.Single(history.OfType<JsonObject>(), item => target == TopHatTarget.OpenAIResponses
				? item["type"]?.GetValue<string>() == "function_call_output" && item["call_id"]?.GetValue<string>() == "prior_internal"
				: item["role"]?.GetValue<string>() == "tool" && item["tool_call_id"]?.GetValue<string>() == "prior_internal");
			Assert.Equal(3, history.Count);
			Assert.False(string.IsNullOrWhiteSpace(result[target == TopHatTarget.OpenAIResponses ? "output" : "content"]!.GetValue<string>()));
		}
	}

	/// <summary>A replayed canonical definition may produce a new private call; routing must preserve response sanitization and caller calls.</summary>
	[Theory]
	[InlineData(TopHatTarget.OpenAIChatCompletions, true)]
	[InlineData(TopHatTarget.OpenAIChatCompletions, false)]
	[InlineData(TopHatTarget.OpenAIResponses, true)]
	[InlineData(TopHatTarget.OpenAIResponses, false)]
	public async Task ReplayedDefinition_InternalResponseCallDoesNotEscapeGateway(TopHatTarget target, bool sharedGatewayHost)
	{
		var original = ReplayedRequest(target, includeResult: true);
		var callerCall = Call(target, "caller_next", "caller_lookup", "{}");
		var (client, inner, provider) = TransformHandlerFactory.Build(services => services.AddTopHatCCR(), configureOptions: options =>
		{
			options.OpenAiBaseUrl = new Uri("https://openai.gateway.test");
			options.AnthropicBaseUrl = new Uri(sharedGatewayHost ? "https://openai.gateway.test" : "https://anthropic.gateway.test");
		}, behavior: async (request, cancellationToken) =>
		{
			Assert.Equal("openai.gateway.test", request.RequestUri!.Host);
			var upstream = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
			Assert.True(JsonNode.DeepEquals(original, upstream));
			var calls = new JsonArray(Call(target, "new_internal", CCRToolDefinition.ToolName, "{\"retrieval_key\":\"replayed-key\"}"), callerCall.DeepClone());
			return Response(target == TopHatTarget.OpenAIResponses
				? new JsonObject { ["id"] = "resp_gateway", ["object"] = "response", ["status"] = "completed", ["output"] = calls }
				: new JsonObject { ["id"] = "chat_gateway", ["object"] = "chat.completion", ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["finish_reason"] = "tool_calls", ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = "Preserve assistant text.", ["tool_calls"] = calls } }) });
		});
		using ((IDisposable)provider)
		using (client)
		using (var request = Request(target, original))
		using (var response = await client.SendAsync(request))
		{
			Assert.True(response.IsSuccessStatusCode);
			Assert.Single(inner.ReceivedRequests);
			var actual = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
			var returnedCalls = target == TopHatTarget.OpenAIResponses ? actual["output"]!.AsArray() : actual["choices"]![0]!["message"]!["tool_calls"]!.AsArray();
			Assert.DoesNotContain(returnedCalls.OfType<JsonObject>(), call => (target == TopHatTarget.OpenAIResponses ? call["name"] : call["function"]?["name"])?.GetValue<string>() == CCRToolDefinition.ToolName);
			Assert.True(JsonNode.DeepEquals(callerCall, Assert.Single(returnedCalls)));
			if (target == TopHatTarget.OpenAIChatCompletions)
			{
				Assert.Equal("Preserve assistant text.", actual["choices"]![0]!["message"]!["content"]!.GetValue<string>());
			}
		}
	}

	/// <summary>Calling a shared gateway directly, including a configured mount prefix, still repairs private history.</summary>
	[Theory]
	[InlineData(TopHatTarget.OpenAIChatCompletions, false)]
	[InlineData(TopHatTarget.OpenAIChatCompletions, true)]
	[InlineData(TopHatTarget.OpenAIResponses, false)]
	[InlineData(TopHatTarget.OpenAIResponses, true)]
	public async Task DirectSharedGateway_RepairsHistory(TopHatTarget target, bool prefixedPath)
	{
		var original = ReplayedRequest(target, includeResult: false);
		var url = "https://gateway.test" + (prefixedPath ? "/openai" : string.Empty) + Path(target) + "?trace=preserve";
		JsonObject? upstream = null;
		var (client, inner, provider) = TransformHandlerFactory.Build(services => services.AddTopHatCCR(), configureOptions: options =>
		{
			options.OpenAiBaseUrl = new Uri("https://gateway.test/openai");
			options.AnthropicBaseUrl = new Uri("https://gateway.test/anthropic");
		}, behavior: async (request, cancellationToken) =>
		{
			Assert.Equal(url, request.RequestUri!.AbsoluteUri);
			upstream = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
			return Response(new JsonObject());
		});
		using ((IDisposable)provider)
		using (client)
		using (var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(original.ToJsonString(), Encoding.UTF8, "application/json") })
		using (var response = await client.SendAsync(request))
		{
			Assert.True(response.IsSuccessStatusCode);
			Assert.Single(inner.ReceivedRequests);
			Assert.NotNull(upstream);
			var history = upstream[target == TopHatTarget.OpenAIResponses ? "input" : "messages"]!.AsArray();
			Assert.Equal(3, history.Count);
			Assert.Equal("prior_internal", history[2]![target == TopHatTarget.OpenAIResponses ? "call_id" : "tool_call_id"]!.GetValue<string>());
			Assert.Equal(target == TopHatTarget.OpenAIResponses ? "function_call_output" : "tool", history[2]![target == TopHatTarget.OpenAIResponses ? "type" : "role"]!.GetValue<string>());
			Assert.True(JsonNode.DeepEquals(original["tools"], upstream["tools"]));
			Assert.True(JsonNode.DeepEquals(original[target == TopHatTarget.OpenAIResponses ? "input" : "messages"]![0], history[0]));
		}
	}

	/// <summary>An outer retry may clone the already rewritten URI; a second dispatch must still sanitize internal calls.</summary>
	[Theory]
	[InlineData(TopHatTarget.OpenAIChatCompletions, false)]
	[InlineData(TopHatTarget.OpenAIChatCompletions, true)]
	[InlineData(TopHatTarget.OpenAIResponses, false)]
	[InlineData(TopHatTarget.OpenAIResponses, true)]
	public async Task RetriedRewrittenGatewayRequest_PreservesResponseBoundary(TopHatTarget target, bool directPrefixedGateway)
	{
		var original = ReplayedRequest(target, includeResult: true);
		var callerCall = Call(target, "caller_next", "caller_lookup", "{}");
		var sourceUrl = (directPrefixedGateway ? "https://gateway.test/openai" : "https://api.openai.com") + Path(target);
		Uri? rewrittenUri = null;
		string? rewrittenBody = null;
		var attempts = 0;
		var (client, inner, provider) = TransformHandlerFactory.Build(services => services.AddTopHatCCR(), configureOptions: options =>
		{
			options.OpenAiBaseUrl = new Uri("https://gateway.test/openai");
			options.AnthropicBaseUrl = new Uri("https://gateway.test/anthropic");
		}, behavior: async (request, cancellationToken) =>
		{
			attempts++;
			Assert.Equal("gateway.test", request.RequestUri!.Host);
			var requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
			Assert.True(JsonNode.DeepEquals(original, JsonNode.Parse(requestBody)));
			if (attempts == 1)
			{
				rewrittenUri = request.RequestUri;
				rewrittenBody = requestBody;
				return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{\"error\":\"retry\"}", Encoding.UTF8, "application/json") };
			}
			Assert.Equal(2, attempts);
			Assert.Equal(rewrittenUri, request.RequestUri);
			return MixedResponse(target, callerCall);
		});
		using ((IDisposable)provider)
		using (client)
		using (var originalRequest = new HttpRequestMessage(HttpMethod.Post, sourceUrl) { Content = new StringContent(original.ToJsonString(), Encoding.UTF8, "application/json") })
		{
			using (var unavailable = await client.SendAsync(originalRequest))
			{
				Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
			}
			Assert.NotNull(rewrittenUri);
			Assert.NotNull(rewrittenBody);
			using (var retry = new HttpRequestMessage(HttpMethod.Post, rewrittenUri) { Content = new StringContent(rewrittenBody, Encoding.UTF8, "application/json") })
			using (var response = await client.SendAsync(retry))
			{
				Assert.True(response.IsSuccessStatusCode);
				Assert.Equal(2, inner.ReceivedRequests.Count);
				var actual = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
				var returnedCalls = target == TopHatTarget.OpenAIResponses ? actual["output"]!.AsArray() : actual["choices"]![0]!["message"]!["tool_calls"]!.AsArray();
				Assert.DoesNotContain(returnedCalls.OfType<JsonObject>(), call => (target == TopHatTarget.OpenAIResponses ? call["name"] : call["function"]?["name"])?.GetValue<string>() == CCRToolDefinition.ToolName);
				Assert.True(JsonNode.DeepEquals(callerCall, Assert.Single(returnedCalls)));
			}
		}
	}

	/// <summary>The provider resolved before rewriting remains authoritative if the destination host also identifies another provider.</summary>
	[Theory]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task RewriteToOtherProviderHost_PreservesOriginalProviderIdentity(TopHatTarget target)
	{
		var callerCall = Call(target, "caller_next", "caller_lookup", "{}");
		var (client, inner, provider) = TransformHandlerFactory.Build(services => services.AddTopHatCCR(),
			configureOptions: options => options.OpenAiBaseUrl = new Uri("https://api.anthropic.com"),
			behavior: (request, _) =>
			{
				Assert.Equal("api.anthropic.com", request.RequestUri!.Host);
				return Task.FromResult(MixedResponse(target, callerCall));
			});
		using ((IDisposable)provider)
		using (client)
		using (var request = Request(target, ReplayedRequest(target, includeResult: true)))
		using (var response = await client.SendAsync(request))
		{
			Assert.True(response.IsSuccessStatusCode);
			Assert.Single(inner.ReceivedRequests);
			var actual = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
			var returnedCalls = target == TopHatTarget.OpenAIResponses ? actual["output"]!.AsArray() : actual["choices"]![0]!["message"]!["tool_calls"]!.AsArray();
			Assert.DoesNotContain(returnedCalls.OfType<JsonObject>(), call => (target == TopHatTarget.OpenAIResponses ? call["name"] : call["function"]?["name"])?.GetValue<string>() == CCRToolDefinition.ToolName);
			Assert.True(JsonNode.DeepEquals(callerCall, Assert.Single(returnedCalls)));
		}
	}

	private static HttpResponseMessage MixedResponse(TopHatTarget target, JsonObject callerCall)
	{
		var calls = new JsonArray(Call(target, "new_internal", CCRToolDefinition.ToolName, "{\"retrieval_key\":\"replayed-key\"}"), callerCall.DeepClone());
		return Response(target == TopHatTarget.OpenAIResponses
			? new JsonObject { ["id"] = "resp_gateway", ["object"] = "response", ["status"] = "completed", ["output"] = calls }
			: new JsonObject { ["id"] = "chat_gateway", ["object"] = "chat.completion", ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["finish_reason"] = "tool_calls", ["message"] = new JsonObject { ["role"] = "assistant", ["tool_calls"] = calls } }) });
	}

	private static JsonObject ReplayedRequest(TopHatTarget target, bool includeResult)
	{
		var isResponses = target == TopHatTarget.OpenAIResponses;
		var callerFunction = new JsonObject { ["name"] = "caller_lookup", ["description"] = "Caller-owned lookup.", ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() } };
		var callerDefinition = isResponses ? callerFunction : new JsonObject { ["function"] = callerFunction };
		callerDefinition["type"] = "function";
		var history = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Preserve caller input." });
		var internalCall = Call(target, "prior_internal", CCRToolDefinition.ToolName, "{\"retrieval_key\":\"replayed-key\"}");
		history.Add(isResponses ? internalCall : new JsonObject { ["role"] = "assistant", ["tool_calls"] = new JsonArray(internalCall) });
		if (includeResult)
		{
			history.Add(isResponses
				? new JsonObject { ["type"] = "function_call_output", ["call_id"] = "prior_internal", ["output"] = "[]" }
				: new JsonObject { ["role"] = "tool", ["tool_call_id"] = "prior_internal", ["content"] = "[]" });
		}
		var body = new JsonObject
		{
			["model"] = "gpt-4o",
			["tools"] = new JsonArray(isResponses ? CCRToolDefinition.BuildOpenAIResponsesToolDefinition() : CCRToolDefinition.BuildOpenAIChatCompletionsToolDefinition(), callerDefinition),
			[isResponses ? "input" : "messages"] = history,
		};
		if (isResponses)
		{
			body["store"] = false;
		}
		return body;
	}

	private static JsonObject Call(TopHatTarget target, string identifier, string name, string arguments) => target == TopHatTarget.OpenAIResponses
		? new JsonObject { ["type"] = "function_call", ["id"] = "fc_" + identifier, ["call_id"] = identifier, ["name"] = name, ["arguments"] = arguments }
		: new JsonObject { ["id"] = identifier, ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments } };

	private static string Path(TopHatTarget target) => target == TopHatTarget.OpenAIResponses ? "/v1/responses" : "/v1/chat/completions";

	private static HttpRequestMessage Request(TopHatTarget target, JsonObject body) => new(HttpMethod.Post, "https://api.openai.com" + Path(target)) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

	private static HttpResponseMessage Response(JsonObject body) => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}
