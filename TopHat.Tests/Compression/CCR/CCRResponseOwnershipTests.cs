using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TopHat.Compression.CCR;
using TopHat.Providers;
using Xunit;

namespace TopHat.Tests.Compression.CCR;

/// <summary>Ensures orchestration releases its active response when inspection fails or is canceled before ownership can reach the caller.</summary>
public sealed class CCRResponseOwnershipTests
{
	/// <summary>Read errors and cancellation release the active response without replacing the original exception.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages, false, false)]
	[InlineData(TopHatTarget.AnthropicMessages, false, true)]
	[InlineData(TopHatTarget.AnthropicMessages, true, false)]
	[InlineData(TopHatTarget.AnthropicMessages, true, true)]
	[InlineData(TopHatTarget.OpenAIChatCompletions, false, false)]
	[InlineData(TopHatTarget.OpenAIChatCompletions, false, true)]
	[InlineData(TopHatTarget.OpenAIChatCompletions, true, false)]
	[InlineData(TopHatTarget.OpenAIChatCompletions, true, true)]
	[InlineData(TopHatTarget.OpenAIResponses, false, false)]
	[InlineData(TopHatTarget.OpenAIResponses, false, true)]
	[InlineData(TopHatTarget.OpenAIResponses, true, false)]
	[InlineData(TopHatTarget.OpenAIResponses, true, true)]
	public async Task FailedResponseInspection_DisposesActiveResponseAndPreservesException(TopHatTarget target, bool afterRetrieval, bool canceledRead)
	{
		using (var cancellation = new CancellationTokenSource())
		using (var request = Request(target))
		{
			Exception expected = canceledRead ? new OperationCanceledException("Canceled while reading provider response.", cancellation.Token) : new HttpRequestException("Provider body read failed.");
			var content = new TrackingContent("{}", expected, canceledRead ? cancellation.Cancel : null);
			using (var active = new HttpResponseMessage(HttpStatusCode.OK) { Content = content })
			using (var initial = afterRetrieval ? JsonResponse(InternalCall(target)) : active)
			{
				var dispatches = 0;
				var context = new CCROrchestrationContext(request, initial, (_, _) =>
				{
					dispatches++;
					Assert.True(afterRetrieval);
					Assert.Equal(1, dispatches);
					return Task.FromResult(active);
				}, "response-ownership", NullLogger.Instance);
				if (canceledRead)
				{
					var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Orchestrator(target).OrchestrateAsync(context, cancellation.Token));
					Assert.Same(expected, actual);
					Assert.Equal(cancellation.Token, actual.CancellationToken);
					Assert.True(cancellation.IsCancellationRequested);
				}
				else
				{
					var actual = await Assert.ThrowsAsync<HttpRequestException>(async () => await Orchestrator(target).OrchestrateAsync(context, cancellation.Token));
					Assert.Same(expected, actual);
				}
				Assert.Equal(1, content.Reads);
				Assert.Equal(afterRetrieval ? 1 : 0, dispatches);
				Assert.True(content.IsDisposed, "The caller received an exception and therefore cannot dispose the active provider response.");
			}
		}
	}

	/// <summary>Cancellation before the inspection loop transfers no response ownership to the caller and reads no response bytes.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task PreCanceledInspection_DisposesResponseWithoutReadingIt(TopHatTarget target)
	{
		var content = new TrackingContent("{}");
		using (var cancellation = new CancellationTokenSource())
		using (var request = Request(target))
		using (var initial = new HttpResponseMessage(HttpStatusCode.OK) { Content = content })
		{
			await request.Content!.LoadIntoBufferAsync();
			cancellation.Cancel();
			var context = new CCROrchestrationContext(request, initial, (_, _) => throw new InvalidOperationException("Canceled inspection must not dispatch a follow-up."), "pre-canceled-response", NullLogger.Instance);
			var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Orchestrator(target).OrchestrateAsync(context, cancellation.Token));
			Assert.Equal(cancellation.Token, error.CancellationToken);
			Assert.Equal(0, content.Reads);
			Assert.True(content.IsDisposed);
		}
	}

	private static ICCROrchestrator Orchestrator(TopHatTarget target)
	{
		var options = Options.Create(new CCROptions { MaxIterations = 1 });
		var store = new InMemoryCompressionContextStore(options);
		return target switch
		{
			TopHatTarget.AnthropicMessages => new AnthropicCCROrchestrator(store, options),
			TopHatTarget.OpenAIChatCompletions => new OpenAIChatCompletionsCCROrchestrator(store, options),
			_ => new OpenAIResponsesCCROrchestrator(store, options),
		};
	}

	private static HttpRequestMessage Request(TopHatTarget target)
	{
		var definition = target switch
		{
			TopHatTarget.AnthropicMessages => CCRToolDefinition.BuildAnthropicToolDefinition(),
			TopHatTarget.OpenAIChatCompletions => CCRToolDefinition.BuildOpenAIChatCompletionsToolDefinition(),
			_ => CCRToolDefinition.BuildOpenAIResponsesToolDefinition(),
		};
		var body = new JsonObject
		{
			["model"] = "test-model",
			["store"] = false,
			["tools"] = new JsonArray(definition),
			[target == TopHatTarget.OpenAIResponses ? "input" : "messages"] = new JsonArray(),
		};
		return new HttpRequestMessage(HttpMethod.Post, "https://provider.test/v1/test") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
	}

	private static JsonObject InternalCall(TopHatTarget target) => target switch
	{
		TopHatTarget.AnthropicMessages => JsonNode.Parse("""{"content":[{"type":"tool_use","id":"internal","name":"tophat_retrieve","input":{"retrieval_key":"missing"}}],"stop_reason":"tool_use"}""")!.AsObject(),
		TopHatTarget.OpenAIChatCompletions => JsonNode.Parse("""{"choices":[{"index":0,"message":{"role":"assistant","tool_calls":[{"type":"function","id":"internal","function":{"name":"tophat_retrieve","arguments":"{\"retrieval_key\":\"missing\"}"}}]},"finish_reason":"tool_calls"}]}""")!.AsObject(),
		_ => JsonNode.Parse("""{"status":"completed","output":[{"type":"function_call","id":"fc_internal","call_id":"internal","name":"tophat_retrieve","arguments":"{\"retrieval_key\":\"missing\"}"}]}""")!.AsObject(),
	};

	private static HttpResponseMessage JsonResponse(JsonObject body) => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

	private sealed class TrackingContent : HttpContent
	{
		private readonly string _body;
		private readonly Exception? _error;
		private readonly Action? _onRead;

		public TrackingContent(string body, Exception? error = null, Action? onRead = null)
		{
			this._body = body;
			this._error = error;
			this._onRead = onRead;
			this.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
		}

		public bool IsDisposed { get; private set; }
		public int Reads { get; private set; }

		protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
		{
			this.Reads++;
			this._onRead?.Invoke();
			return this._error is null ? stream.WriteAsync(Encoding.UTF8.GetBytes(this._body)).AsTask() : Task.FromException(this._error);
		}

		protected override bool TryComputeLength(out long length)
		{
			length = 0;
			return false;
		}

		protected override void Dispose(bool disposing)
		{
			this.IsDisposed = true;
			base.Dispose(disposing);
		}
	}
}
