using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TopHat.Compression.CCR;
using TopHat.Compression.CCR.DependencyInjection;
using TopHat.DependencyInjection;
using TopHat.Handlers;
using TopHat.Relevance.BM25.DependencyInjection;
using TopHat.Tests.Support;
using TopHat.Transforms;
using Xunit;

namespace TopHat.Tests.Compression.CCR;

/// <summary>Exercises a normal outer retry through the real handler and built-in cache transform.</summary>
public sealed class CCRRetryTests
{
	/// <summary>Retrying the same request must retain ownership of the decorated tool TopHat previously injected.</summary>
	[Fact]
	public async Task OuterRetry_PreservesOwnershipOfPreviouslyInjectedDecoratedTool()
	{
		var dispatches = 0;
		string? retrievalKey = null;
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddTopHat();
		services.AddTopHatCCR();
		services.AddTopHatBm25Relevance();
		services.AddTopHatJsonContextCompressor(options =>
		{
			options.MinTokensToCrush = 1;
			options.UnfrozenMessageCount = int.MaxValue;
		});
		services.AddTopHatAnthropicCacheAligner(configureRegistration: options => options.WithOrder(300));
		services.AddHttpClient("retry-audit")
			.AddHttpMessageHandler(() => new RetryOnceHandler())
			.AddHttpMessageHandler<TopHatHandler>()
			.ConfigurePrimaryHttpMessageHandler(() => new MockInnerHandler(async (request, cancellationToken) =>
			{
				dispatches++;
				Assert.True(dispatches <= 3);
				var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
				if (dispatches <= 2)
				{
					var synthetic = Assert.Single(body["tools"]!.AsArray().OfType<JsonObject>(), tool => tool["name"]!.GetValue<string>() == "tophat_retrieve");
					Assert.Equal("ephemeral", synthetic["cache_control"]!["type"]!.GetValue<string>());
					var compressed = JsonNode.Parse(body["messages"]![2]!["content"]![0]!["content"]!.GetValue<string>())!.AsArray();
					var metadata = Assert.Single(compressed.OfType<JsonObject>(), item => item.ContainsKey("_tophat_compression"));
					retrievalKey = metadata["_tophat_compression"]!["retrieval_key"]!.GetValue<string>();
				}
				if (dispatches == 1)
				{
					return Response(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"type\":\"overloaded_error\",\"message\":\"retry\"}}");
				}
				if (dispatches == 2)
				{
					Assert.NotNull(retrievalKey);
					return Response(HttpStatusCode.OK, new JsonObject
					{
						["id"] = "msg_retry", ["type"] = "message", ["role"] = "assistant", ["stop_reason"] = "tool_use",
						["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_retry", ["name"] = "tophat_retrieve", ["input"] = new JsonObject { ["retrieval_key"] = retrievalKey } }),
					}.ToJsonString());
				}
				return Response(HttpStatusCode.OK, "{\"id\":\"msg_done\",\"type\":\"message\",\"role\":\"assistant\",\"stop_reason\":\"end_turn\",\"content\":[{\"type\":\"text\",\"text\":\"Done.\"}]}");
			}));

		var payload = JsonSerializer.Serialize(Enumerable.Range(1, 100).Select(index => new { id = index, status = "processed", message = "record inserted successfully into database" }));
		var requestBody = new JsonObject
		{
			["model"] = "claude-sonnet-4-5-20250929", ["max_tokens"] = 512,
			["tools"] = new JsonArray(new JsonObject { ["name"] = "get_logs", ["description"] = new string('x', 6000), ["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() } }),
			["messages"] = new JsonArray
			{
				new JsonObject { ["role"] = "user", ["content"] = "summarize logs" },
				new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = "caller_logs", ["name"] = "get_logs", ["input"] = new JsonObject() }) },
				new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = "caller_logs", ["content"] = payload }) },
			},
		};
		using (var provider = services.BuildServiceProvider())
		using (var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("retry-audit"))
		using (var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages") { Content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json") })
		using (var response = await client.SendAsync(request))
		{
			var final = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
			Assert.DoesNotContain(final["content"]!.AsArray().OfType<JsonObject>(), item => item["name"]?.GetValue<string>() == "tophat_retrieve");
			Assert.Equal(3, dispatches);
			Assert.Equal("end_turn", final["stop_reason"]!.GetValue<string>());
		}
	}

	/// <summary>Both reused requests and clones carrying Options preserve ownership across multiple attempts.</summary>
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RepeatedRetries_PreserveOwnedToolAndFulfillRetrieval(bool cloneRequest)
	{
		var observed = new List<JsonObject>();
		var result = await SendWithRetriesAsync(CreateBody(), 2, cloneRequest, null, (body, attempt) =>
		{
			observed.Add(body);
			Assert.True(attempt <= 4);
			if (attempt <= 3)
			{
				var tool = Assert.Single(body["tools"]!.AsArray().OfType<JsonObject>(), item => item["name"]!.GetValue<string>() == "tophat_retrieve");
				Assert.Equal("ephemeral", tool["cache_control"]!["type"]!.GetValue<string>());
			}
			return attempt <= 2 ? Response(HttpStatusCode.ServiceUnavailable, "{}") : attempt == 3 ? RetrievalResponse(body) : Response(HttpStatusCode.OK, "{\"content\":[{\"type\":\"text\",\"text\":\"done\"}],\"stop_reason\":\"end_turn\"}");
		});
		Assert.Equal(4, observed.Count);
		Assert.DoesNotContain("tophat_retrieve", result, StringComparison.Ordinal);
		var toolResult = observed[^1]["messages"]!.AsArray()[^1]!["content"]![0]!;
		Assert.Equal("retry_call", toolResult["tool_use_id"]!.GetValue<string>());
		Assert.NotEmpty(JsonNode.Parse(toolResult["content"]!.GetValue<string>())!.AsArray());
	}

	/// <summary>Changing eligibility on retry restores the data before the first compression, including when compression no longer runs.</summary>
	[Theory]
	[InlineData(false, false)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public async Task RetryEnablesStreaming_RestoresOriginalPayloadAndWithdrawsTool(bool cloneRequest, bool skipCompressorOnRetry)
	{
		var source = CreateBody();
		var originalPayload = Payload(source);
		var observed = new List<JsonObject>();
		await SendWithRetriesAsync(source, 1, cloneRequest, (body, _) => body["stream"] = true, (body, attempt) =>
		{
			observed.Add(body);
			if (attempt == 1)
			{
				Assert.NotEqual(originalPayload, Payload(body));
				Assert.Contains("retrieval_key", Payload(body), StringComparison.Ordinal);
				return Response(HttpStatusCode.ServiceUnavailable, "{}");
			}
			Assert.Equal(2, attempt);
			Assert.Equal(originalPayload, Payload(body));
			Assert.True(body["stream"]!.GetValue<bool>());
			Assert.DoesNotContain(body["tools"]!.AsArray().OfType<JsonObject>(), tool => tool["name"]!.GetValue<string>() == "tophat_retrieve");
			return Response(HttpStatusCode.OK, "{}");
		}, context => !skipCompressorOnRetry || context.Body?["stream"]?.GetValue<bool>() != true);
		Assert.Equal(2, observed.Count);
	}

	/// <summary>Copied provenance does not authorize an unrelated caller tool with the same name and decorations.</summary>
	[Fact]
	public async Task RetryWithUnrelatedBodyAndCopiedOptions_PreservesCallerOwnedDecoratedTool()
	{
		var dispatches = 0;
		var callerTool = new JsonObject
		{
			["name"] = "tophat_retrieve", ["description"] = "Caller-owned lookup " + new string('y', 6000),
			["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["query"] = new JsonObject { ["type"] = "string" } } },
			["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
		};
		var callerResponse = "{\"content\":[{\"type\":\"tool_use\",\"id\":\"caller_lookup\",\"name\":\"tophat_retrieve\",\"input\":{\"query\":\"caller operation\"}}],\"stop_reason\":\"tool_use\"}";
		var final = await SendWithRetriesAsync(CreateBody(), 1, true, (body, _) =>
		{
			body["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "A different operation with a caller-owned tool." });
			body["tools"] = new JsonArray(callerTool.DeepClone());
		}, (body, attempt) =>
		{
			dispatches++;
			if (attempt == 1)
			{
				return Response(HttpStatusCode.ServiceUnavailable, "{}");
			}
			Assert.Equal(2, attempt);
			Assert.True(JsonNode.DeepEquals(callerTool, Assert.Single(body["tools"]!.AsArray())));
			Assert.Single(body["messages"]!.AsArray());
			return Response(HttpStatusCode.OK, callerResponse);
		});
		Assert.Equal(2, dispatches);
		Assert.Equal(callerResponse, final);
	}

	/// <summary>Changing owned semantics or compressed data while its private keys survive cannot discard provenance.</summary>
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RetryWithAlteredOwnedArtifacts_RejectsBeforeSecondDispatch(bool alterPayload)
	{
		var dispatches = 0;
		await Assert.ThrowsAsync<HttpRequestException>(() => SendWithRetriesAsync(CreateBody(), 1, true, (body, _) =>
		{
			if (alterPayload)
			{
				var payload = JsonNode.Parse(Payload(body))!.AsArray();
				payload[0]!["message"] = "changed after compression";
				body["messages"]![2]!["content"]![0]!["content"] = payload.ToJsonString();
			}
			else
			{
				var tool = body["tools"]!.AsArray().OfType<JsonObject>().Single(item => item["name"]!.GetValue<string>() == "tophat_retrieve");
				tool["description"] = "Replaced caller semantics";
			}
			body["stream"] = true;
		}, (_, _) =>
		{
			dispatches++;
			return Response(HttpStatusCode.ServiceUnavailable, "{}");
		}));
		Assert.Equal(1, dispatches);
	}

	/// <summary>Changing routing cannot escape inspection of private artifacts already present on a retried request.</summary>
	[Theory]
	[InlineData("https://api.anthropic.com/v1/unknown")]
	[InlineData("https://unrelated.example/v1/messages")]
	public async Task RetryWithUnknownTarget_RejectsOwnedArtifactsBeforeDispatch(string endpoint)
	{
		var dispatches = 0;
		await Assert.ThrowsAsync<HttpRequestException>(() => SendWithRetriesAsync(CreateBody(), 1, true, null, (_, _) =>
		{
			dispatches++;
			return Response(HttpStatusCode.ServiceUnavailable, "{}");
		}, retryUri: new Uri(endpoint)));
		Assert.Equal(1, dispatches);
	}

	/// <summary>Restoring a replayed canonical definition preserves prior retrieval data and caller tool edits through multiple retries.</summary>
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RetriedCanonicalDefinition_PreservesCallerToolOrderAndEdits(bool cloneRequest)
	{
		const string compressedPayload = "[{\"id\":1},{\"_tophat_compression\":{\"retrieval_key\":\"prior-retry-key\",\"dropped_count\":1}}]";
		var source = CreateBody();
		source["tools"]!.AsArray().Add(CCRToolDefinition.BuildAnthropicToolDefinition());
		source["messages"]![2]!["content"]![0]!["content"] = compressedPayload;
		var expectedToolNames = new List<string> { "get_logs", "tophat_retrieve" };
		var dispatches = 0;
		var result = await SendWithRetriesAsync(source, 2, cloneRequest, (body, attempt) =>
		{
			var callerTool = body["tools"]!.AsArray().OfType<JsonObject>().Single(tool => tool["name"]!.GetValue<string>() == "get_logs");
			callerTool["description"] = new string('x', 6000) + " caller edit " + attempt.ToString(System.Globalization.CultureInfo.InvariantCulture);
			var addedName = "retry_tool_" + attempt.ToString(System.Globalization.CultureInfo.InvariantCulture);
			body["tools"]!.AsArray().Insert(0, new JsonObject { ["name"] = addedName, ["input_schema"] = new JsonObject { ["type"] = "object" } });
			expectedToolNames.Insert(0, addedName);
		}, (body, attempt) =>
		{
			dispatches++;
			Assert.True(attempt <= 4);
			Assert.Equal(expectedToolNames, body["tools"]!.AsArray().OfType<JsonObject>().Select(tool => tool["name"]!.GetValue<string>()));
			Assert.Equal(compressedPayload, Payload(body));
			var privateTool = Assert.Single(body["tools"]!.AsArray().OfType<JsonObject>(), tool => tool["name"]!.GetValue<string>() == "tophat_retrieve");
			Assert.Equal("ephemeral", privateTool["cache_control"]!["type"]!.GetValue<string>());
			if (attempt > 1)
			{
				var callerTool = body["tools"]!.AsArray().OfType<JsonObject>().Single(tool => tool["name"]!.GetValue<string>() == "get_logs");
				Assert.Equal(new string('x', 6000) + " caller edit " + Math.Min(attempt - 1, 2).ToString(System.Globalization.CultureInfo.InvariantCulture), callerTool["description"]!.GetValue<string>());
			}
			if (attempt <= 2)
			{
				return Response(HttpStatusCode.ServiceUnavailable, "{}");
			}
			if (attempt == 3)
			{
				return RetrievalResponse(body);
			}
			var output = body["messages"]!.AsArray()[^1]!["content"]![0]!;
			Assert.Equal("retry_call", output["tool_use_id"]!.GetValue<string>());
			var restored = Assert.Single(JsonNode.Parse(output["content"]!.GetValue<string>())!.AsArray());
			Assert.Equal(42, restored!["id"]!.GetValue<int>());
			Assert.Equal("old omitted item", restored["message"]!.GetValue<string>());
			return Response(HttpStatusCode.OK, "{\"content\":[{\"type\":\"text\",\"text\":\"done\"}],\"stop_reason\":\"end_turn\"}");
		}, _ => false, initializeProvider: provider => provider.GetRequiredService<ICompressionContextStore>().Store("prior-retry-key", new JsonNode[] { new JsonObject { ["id"] = 42, ["message"] = "old omitted item" } }));
		Assert.Equal(4, dispatches);
		Assert.DoesNotContain("tophat_retrieve", result, StringComparison.Ordinal);
	}

	/// <summary>Older compression metadata remains ownership evidence when a retry replaces a replayed tool's semantics.</summary>
	[Fact]
	public async Task RetriedCanonicalDefinition_WithAlteredSchemaAndOldKey_RejectsBeforeDispatch()
	{
		var source = CreateBody();
		source["tools"]!.AsArray().Add(CCRToolDefinition.BuildAnthropicToolDefinition());
		source["messages"]![2]!["content"]![0]!["content"] = "[{\"_tophat_compression\":{\"retrieval_key\":\"prior-retry-key\",\"dropped_count\":1}}]";
		var dispatches = 0;
		await Assert.ThrowsAsync<HttpRequestException>(() => SendWithRetriesAsync(source, 1, true, (body, _) =>
		{
			var privateTool = body["tools"]!.AsArray().OfType<JsonObject>().Single(tool => tool["name"]!.GetValue<string>() == "tophat_retrieve");
			privateTool["description"] = "Replacement semantics after the first attempt.";
		}, (_, _) =>
		{
			dispatches++;
			return Response(HttpStatusCode.ServiceUnavailable, "{}");
		}, _ => false, initializeProvider: provider => provider.GetRequiredService<ICompressionContextStore>().Store("prior-retry-key", new JsonNode[] { new JsonObject { ["id"] = 42 } })));
		Assert.Equal(1, dispatches);
	}

	private static async Task<string> SendWithRetriesAsync(JsonObject body, int retries, bool cloneRequest, Action<JsonObject, int>? mutate, Func<JsonObject, int, HttpResponseMessage> upstream,
		Func<RequestTransformContext, bool>? compressorFilter = null, Uri? retryUri = null, Action<ServiceProvider>? initializeProvider = null)
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddTopHat();
		services.AddTopHatCCR();
		services.AddTopHatBm25Relevance();
		services.AddTopHatJsonContextCompressor(options =>
		{
			options.MinTokensToCrush = 1;
			options.UnfrozenMessageCount = int.MaxValue;
		}, registration =>
		{
			if (compressorFilter is not null)
			{
				registration.AppliesTo(compressorFilter);
			}
		});
		services.AddTopHatAnthropicCacheAligner(configureRegistration: options => options.WithOrder(300));
		var dispatches = 0;
		services.AddHttpClient("retry-controls")
			.AddHttpMessageHandler(() => new ScriptedRetryHandler(retries, cloneRequest, mutate, retryUri))
			.AddHttpMessageHandler<TopHatHandler>()
			.ConfigurePrimaryHttpMessageHandler(() => new MockInnerHandler(async (request, cancellationToken) =>
			{
				dispatches++;
				return upstream(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject(), dispatches);
			}));
		using (var provider = services.BuildServiceProvider())
		{
			initializeProvider?.Invoke(provider);
			using (var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("retry-controls"))
			using (var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") })
			using (var response = await client.SendAsync(request))
			{
				return await response.Content.ReadAsStringAsync();
			}
		}
	}

	private static JsonObject CreateBody()
	{
		var payload = JsonSerializer.Serialize(Enumerable.Range(1, 100).Select(index => new { id = index, status = "processed", message = "record inserted successfully into database" }));
		return new JsonObject
		{
			["model"] = "claude-sonnet-4-5-20250929", ["max_tokens"] = 512,
			["tools"] = new JsonArray(new JsonObject { ["name"] = "get_logs", ["description"] = new string('x', 6000), ["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() } }),
			["messages"] = new JsonArray
			{
				new JsonObject { ["role"] = "user", ["content"] = "summarize logs" },
				new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = "caller_logs", ["name"] = "get_logs", ["input"] = new JsonObject() }) },
				new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = "caller_logs", ["content"] = payload }) },
			},
		};
	}

	private static string Payload(JsonObject body) => body["messages"]![2]!["content"]![0]!["content"]!.GetValue<string>();

	private static HttpResponseMessage RetrievalResponse(JsonObject body)
	{
		var metadata = JsonNode.Parse(Payload(body))!.AsArray().OfType<JsonObject>().Last(item => item.ContainsKey("_tophat_compression"));
		return Response(HttpStatusCode.OK, new JsonObject
		{
			["content"] = new JsonArray(new JsonObject
			{
				["type"] = "tool_use", ["id"] = "retry_call", ["name"] = "tophat_retrieve",
				["input"] = new JsonObject { ["retrieval_key"] = metadata["_tophat_compression"]!["retrieval_key"]!.DeepClone() },
			}),
			["stop_reason"] = "tool_use",
		}.ToJsonString());
	}

	private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

	private sealed class ScriptedRetryHandler : DelegatingHandler
	{
		private readonly int _retries;
		private readonly bool _cloneRequest;
		private readonly Action<JsonObject, int>? _mutate;
		private readonly Uri? _retryUri;

		/// <summary>Creates a retry policy that can clone Options and change eligibility or routing between attempts.</summary>
		public ScriptedRetryHandler(int retries, bool cloneRequest, Action<JsonObject, int>? mutate, Uri? retryUri)
		{
			this._retries = retries;
			this._cloneRequest = cloneRequest;
			this._mutate = mutate;
			this._retryUri = retryUri;
		}

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var current = request;
			var clones = new List<HttpRequestMessage>();
			try
			{
				for (var attempt = 0; ; attempt++)
				{
					var response = await base.SendAsync(current, cancellationToken);
					if (response.StatusCode != HttpStatusCode.ServiceUnavailable || attempt >= this._retries)
					{
						return response;
					}
					response.Dispose();
					var body = JsonNode.Parse(await current.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
					this._mutate?.Invoke(body, attempt + 1);
					if (this._cloneRequest)
					{
						var clone = new HttpRequestMessage(current.Method, current.RequestUri);
						foreach (var header in current.Headers)
						{
							clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
						}
						foreach (var option in current.Options)
						{
							clone.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
						}
						clones.Add(clone);
						current = clone;
					}
					current.Content?.Dispose();
					current.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
					if (this._retryUri is not null)
					{
						current.RequestUri = this._retryUri;
					}
				}
			}
			finally
			{
				foreach (var clone in clones)
				{
					clone.Dispose();
				}
			}
		}
	}

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
