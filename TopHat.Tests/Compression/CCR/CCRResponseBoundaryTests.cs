using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TopHat.Compression.CCR;
using TopHat.Providers;
using Xunit;

namespace TopHat.Tests.Compression.CCR;

/// <summary>Checks fail-closed response handling without changing ordinary non-CCR response behavior.</summary>
public sealed class CCRResponseBoundaryTests
{
	/// <summary>Provider, unsafe format, and initial/retrieval/exhaustion response boundaries.</summary>
	public static IEnumerable<object[]> UnsafeCases()
	{
		foreach (var target in Targets())
		{
			foreach (var format in new[] { "sse", "malformed", "nonobject", "text" })
			{
				foreach (var stage in new[] { "initial", "retrieval", "exhaustion" })
				{
					yield return new object[] { target, format, stage };
				}
			}
		}
	}

	/// <summary>Once a private tool is advertised, every successful response must be safely inspectable.</summary>
	[Theory]
	[MemberData(nameof(UnsafeCases))]
	public async Task OwnedTool_UninspectableSuccessIsRejectedAndDisposed(TopHatTarget target, string format, string stage)
	{
		var content = UnsafeContent(format);
		using (var request = Request(target, advertisePrivateTool: true))
		using (var unsafeResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = content })
		using (var initial = stage == "initial" ? unsafeResponse : JsonResponse(Call(target)))
		{
			var dispatches = 0;
			var context = new CCROrchestrationContext(request, initial, async (followUp, cancellationToken) =>
			{
				dispatches++;
				Assert.Equal(1, dispatches);
				var body = JsonNode.Parse(await followUp.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
				Assert.Equal(stage != "exhaustion", body["tools"]!.AsArray().Count > 0);
				return unsafeResponse;
			}, "boundary", NullLogger.Instance);
			var error = await Assert.ThrowsAsync<HttpRequestException>(async () => await Orchestrator(target, stage == "exhaustion" ? 0 : 1).OrchestrateAsync(context, CancellationToken.None));
			Assert.Contains("safely", error.Message, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("tophat_retrieve", error.ToString(), StringComparison.Ordinal);
			Assert.True(content.IsDisposed);
			Assert.Equal(stage == "initial" ? 0 : 1, dispatches);
			Assert.Equal(format is "sse" or "text" ? 0 : 1, content.Reads);
		}
	}

	/// <summary>Provider/format combinations that must remain untouched without private tool advertisement.</summary>
	public static IEnumerable<object[]> OrdinaryCases() => Targets().SelectMany(target => new[] { "sse", "malformed", "nonobject", "text" }.Select(format => new object[] { target, format }));

	/// <summary>Unsupported formats remain the caller's concern when TopHat has introduced no private tool.</summary>
	[Theory]
	[MemberData(nameof(OrdinaryCases))]
	public async Task NoPrivateTool_UnsupportedResponsePassesThroughUnchanged(TopHatTarget target, string format)
	{
		var content = UnsafeContent(format);
		using (var request = Request(target, advertisePrivateTool: false))
		using (var initial = new HttpResponseMessage(HttpStatusCode.OK) { Content = content })
		{
			var context = new CCROrchestrationContext(request, initial, (_, _) => throw new InvalidOperationException("Unexpected follow-up."), "ordinary", NullLogger.Instance);
			using (var result = await Orchestrator(target).OrchestrateAsync(context, CancellationToken.None))
			{
				Assert.Same(initial, result);
				Assert.Same(content, result.Content);
				Assert.False(content.IsDisposed);
				Assert.Equal(content.Original, await result.Content.ReadAsStringAsync());
			}
		}
	}

	/// <summary>Retryable HTTP errors keep their status, headers, and complete error body after advertisement.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task OwnedTool_ErrorStatusRetainsCallerRetrySemantics(TopHatTarget target)
	{
		const string body = "{\"error\":{\"message\":\"retry later\"}}";
		using (var request = Request(target, advertisePrivateTool: true))
		using (var initial = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent(body, Encoding.UTF8, "application/json") })
		{
			initial.Headers.TryAddWithoutValidation("Retry-After", "1");
			var context = new CCROrchestrationContext(request, initial, (_, _) => throw new InvalidOperationException("Unexpected follow-up."), "error", NullLogger.Instance);
			using (var result = await Orchestrator(target).OrchestrateAsync(context, CancellationToken.None))
			{
				Assert.Same(initial, result);
				Assert.Equal(HttpStatusCode.ServiceUnavailable, result.StatusCode);
				Assert.Equal("1", result.Headers.GetValues("Retry-After").Single());
				Assert.Equal(body, await result.Content.ReadAsStringAsync());
			}
		}
	}

	private static TopHatTarget[] Targets() => new[] { TopHatTarget.AnthropicMessages, TopHatTarget.OpenAIChatCompletions, TopHatTarget.OpenAIResponses };

	private static ICCROrchestrator Orchestrator(TopHatTarget target, int allowance = 1)
	{
		var options = Options.Create(new CCROptions { MaxIterations = allowance });
		var store = new InMemoryCompressionContextStore(options);
		return target switch
		{
			TopHatTarget.AnthropicMessages => new AnthropicCCROrchestrator(store, options),
			TopHatTarget.OpenAIChatCompletions => new OpenAIChatCompletionsCCROrchestrator(store, options),
			_ => new OpenAIResponsesCCROrchestrator(store, options),
		};
	}

	private static HttpRequestMessage Request(TopHatTarget target, bool advertisePrivateTool)
	{
		var body = new JsonObject { ["model"] = "test", [target == TopHatTarget.OpenAIResponses ? "input" : "messages"] = new JsonArray(), ["store"] = false };
		if (advertisePrivateTool)
		{
			body["tools"] = new JsonArray(target switch
			{
				TopHatTarget.AnthropicMessages => CCRToolDefinition.BuildAnthropicToolDefinition(),
				TopHatTarget.OpenAIChatCompletions => CCRToolDefinition.BuildOpenAIChatCompletionsToolDefinition(),
				_ => CCRToolDefinition.BuildOpenAIResponsesToolDefinition(),
			});
		}
		return new HttpRequestMessage(HttpMethod.Post, "https://provider.test/v1/test") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
	}

	private static JsonObject Call(TopHatTarget target) => target switch
	{
		TopHatTarget.AnthropicMessages => JsonNode.Parse("""{"content":[{"type":"tool_use","id":"private","name":"tophat_retrieve","input":{"retrieval_key":"key"}}],"stop_reason":"tool_use"}""")!.AsObject(),
		TopHatTarget.OpenAIChatCompletions => JsonNode.Parse("""{"choices":[{"index":0,"message":{"role":"assistant","tool_calls":[{"type":"function","id":"private","function":{"name":"tophat_retrieve","arguments":"{\"retrieval_key\":\"key\"}"}}]},"finish_reason":"tool_calls"}]}""")!.AsObject(),
		_ => JsonNode.Parse("""{"status":"completed","output":[{"type":"function_call","id":"fc_private","call_id":"private","name":"tophat_retrieve","arguments":"{\"retrieval_key\":\"key\"}"}]}""")!.AsObject(),
	};

	private static HttpResponseMessage JsonResponse(JsonObject body) => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

	private static TrackingContent UnsafeContent(string format) => format switch
	{
		"sse" => new TrackingContent("data: {\"name\":\"tophat_retrieve\"}\n\n", "text/event-stream"),
		"malformed" => new TrackingContent("{\"name\":\"tophat_retrieve\"", "application/json"),
		"nonobject" => new TrackingContent("[{\"name\":\"tophat_retrieve\"}]", "application/json"),
		_ => new TrackingContent("tophat_retrieve", "text/plain"),
	};

	private sealed class TrackingContent : HttpContent
	{
		public TrackingContent(string original, string mediaType)
		{
			this.Original = original;
			this.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
		}

		public string Original { get; }
		public bool IsDisposed { get; private set; }
		public int Reads { get; private set; }

		protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
		{
			this.Reads++;
			return stream.WriteAsync(Encoding.UTF8.GetBytes(this.Original)).AsTask();
		}

		protected override bool TryComputeLength(out long length)
		{
			length = Encoding.UTF8.GetByteCount(this.Original);
			return true;
		}

		protected override void Dispose(bool disposing)
		{
			this.IsDisposed = true;
			base.Dispose(disposing);
		}
	}
}
