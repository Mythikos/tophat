using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TopHat.Compression.CCR;
using TopHat.Compression.CCR.DependencyInjection;
using TopHat.DependencyInjection;
using TopHat.Handlers;
using TopHat.Tests.Support;
using Xunit;

namespace TopHat.Tests.Compression.CCR;

/// <summary>Preserves ownership evidence for canonical tools replayed from earlier requests without performing new compression.</summary>
public sealed class CCRReplayOwnershipTests
{
	/// <summary>A built-in transform may decorate a replayed canonical definition without making its private response calls caller-owned.</summary>
	[Fact]
	public async Task CanonicalReplay_CacheDecorationDoesNotExposePrivateCall()
	{
		var dispatches = 0;
		var callerCall = new JsonObject { ["type"] = "tool_use", ["id"] = "caller_next", ["name"] = "get_logs", ["input"] = new JsonObject { ["query"] = "caller request" } };
		var expectedContent = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Keep caller text." }, callerCall.DeepClone());
		var (client, provider) = Build(async (request, cancellationToken) =>
		{
			dispatches++;
			Assert.Equal(1, dispatches);
			var upstream = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
			AssertDecoratedPrivateTool(upstream);
			var content = (JsonArray)expectedContent.DeepClone();
			content.Insert(1, PrivateCall());
			return Response(HttpStatusCode.OK, new JsonObject { ["content"] = content, ["stop_reason"] = "tool_use" });
		});
		using (provider)
		using (client)
		using (var request = Request(CreateBody(CCRToolDefinition.BuildAnthropicToolDefinition())))
		using (var response = await client.SendAsync(request))
		{
			Assert.True(response.IsSuccessStatusCode);
			Assert.Equal(1, dispatches);
			var actual = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
			Assert.True(JsonNode.DeepEquals(expectedContent, actual["content"]));
			Assert.Equal("tool_use", actual["stop_reason"]!.GetValue<string>());
		}
	}

	/// <summary>A definition already decorated by the caller has no canonical ownership evidence and remains the caller's tool.</summary>
	[Fact]
	public async Task CallerDecoratedDefinitionWithoutProvenance_PreservesCallerCall()
	{
		var callerDefinition = CCRToolDefinition.BuildAnthropicToolDefinition();
		callerDefinition["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
		var expected = new JsonObject { ["content"] = new JsonArray(PrivateCall()), ["stop_reason"] = "tool_use", ["caller_metadata"] = "preserve unchanged" };
		var expectedText = expected.ToJsonString();
		var dispatches = 0;
		var (client, provider) = Build(async (request, cancellationToken) =>
		{
			dispatches++;
			Assert.Equal(1, dispatches);
			var upstream = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
			Assert.True(JsonNode.DeepEquals(callerDefinition, AssertDecoratedPrivateTool(upstream)));
			return Response(HttpStatusCode.OK, expected);
		});
		using (provider)
		using (client)
		using (var request = Request(CreateBody(callerDefinition)))
		using (var response = await client.SendAsync(request))
		{
			Assert.True(response.IsSuccessStatusCode);
			Assert.Equal(1, dispatches);
			Assert.Equal(expectedText, await response.Content.ReadAsStringAsync());
		}
	}

	/// <summary>Retry restoration must retain a replayed tool when its prior compression still depends on an existing retrieval key.</summary>
	[Fact]
	public async Task RetriedCanonicalReplay_PreservesPriorRetrievalCapability()
	{
		const string compressedPayload = "[{\"id\":1,\"message\":\"retained\"},{\"_tophat_compression\":{\"retrieval_key\":\"prior-key\",\"dropped_count\":1}}]";
		var body = CreateBody(CCRToolDefinition.BuildAnthropicToolDefinition());
		body["messages"]!.AsArray().Add(new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = "prior_logs", ["name"] = "get_logs", ["input"] = new JsonObject() }) });
		body["messages"]!.AsArray().Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = "prior_logs", ["content"] = compressedPayload }) });
		var dispatches = 0;
		var (client, provider) = Build(async (request, cancellationToken) =>
		{
			dispatches++;
			Assert.True(dispatches <= 3);
			var upstream = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
			Assert.Equal(compressedPayload, upstream["messages"]![2]!["content"]![0]!["content"]!.GetValue<string>());
			AssertDecoratedPrivateTool(upstream);
			if (dispatches == 1)
			{
				return Response(HttpStatusCode.ServiceUnavailable, new JsonObject { ["error"] = "retry" });
			}
			if (dispatches == 2)
			{
				return Response(HttpStatusCode.OK, new JsonObject { ["content"] = new JsonArray(PrivateCall()), ["stop_reason"] = "tool_use" });
			}
			var result = upstream["messages"]!.AsArray()[^1]!["content"]![0]!;
			Assert.Equal("private_replay", result["tool_use_id"]!.GetValue<string>());
			var restoredItem = Assert.Single(JsonNode.Parse(result["content"]!.GetValue<string>())!.AsArray());
			Assert.Equal(42, restoredItem!["id"]!.GetValue<int>());
			Assert.Equal("Previously omitted record.", restoredItem["message"]!.GetValue<string>());
			return Response(HttpStatusCode.OK, new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Recovered prior record." }), ["stop_reason"] = "end_turn" });
		}, retry: true);
		using (provider)
		using (client)
		{
			provider.GetRequiredService<ICompressionContextStore>().Store("prior-key", new JsonNode[] { new JsonObject { ["id"] = 42, ["message"] = "Previously omitted record." } });
			using (var request = Request(body))
			using (var response = await client.SendAsync(request))
			{
				Assert.True(response.IsSuccessStatusCode);
				Assert.Equal(3, dispatches);
				var actual = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
				Assert.Equal("end_turn", actual["stop_reason"]!.GetValue<string>());
				Assert.Equal("Recovered prior record.", Assert.Single(actual["content"]!.AsArray())!["text"]!.GetValue<string>());
			}
		}
	}

	private static (HttpClient Client, ServiceProvider Provider) Build(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> behavior, bool retry = false)
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddTopHat();
		services.AddTopHatCCR();
		services.AddTopHatAnthropicCacheAligner();
		// No compressor is registered: ownership and old retrieval data must survive without fresh injection.
		var builder = services.AddHttpClient("canonical-replay");
		if (retry)
		{
			builder.AddHttpMessageHandler(() => new RetryOnceHandler());
		}
		builder.AddHttpMessageHandler<TopHatHandler>().ConfigurePrimaryHttpMessageHandler(() => new MockInnerHandler(behavior));
		var provider = services.BuildServiceProvider();
		return (provider.GetRequiredService<IHttpClientFactory>().CreateClient("canonical-replay"), provider);
	}

	private static JsonObject CreateBody(JsonObject reservedDefinition) => new()
	{
		["model"] = "claude-sonnet-4-5-20250929",
		["max_tokens"] = 512,
		["tools"] = new JsonArray(new JsonObject { ["name"] = "get_logs", ["description"] = new string('x', 6000), ["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() } }, reservedDefinition.DeepClone()),
		["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Recover records if needed." }),
	};

	private static JsonObject PrivateCall() => new() { ["type"] = "tool_use", ["id"] = "private_replay", ["name"] = "tophat_retrieve", ["input"] = new JsonObject { ["retrieval_key"] = "prior-key" } };

	private static JsonObject AssertDecoratedPrivateTool(JsonObject body)
	{
		var definition = Assert.Single(body["tools"]!.AsArray().OfType<JsonObject>(), tool => tool["name"]!.GetValue<string>() == "tophat_retrieve");
		Assert.Equal("ephemeral", definition["cache_control"]!["type"]!.GetValue<string>());
		return definition;
	}

	private static HttpRequestMessage Request(JsonObject body) => new(HttpMethod.Post, "https://api.anthropic.com/v1/messages") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

	private static HttpResponseMessage Response(HttpStatusCode status, JsonObject body) => new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

	private sealed class RetryOnceHandler : DelegatingHandler
	{
		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var initial = await base.SendAsync(request, cancellationToken);
			if (initial.StatusCode != HttpStatusCode.ServiceUnavailable)
			{
				return initial;
			}
			initial.Dispose();
			return await base.SendAsync(request, cancellationToken);
		}
	}
}
