using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using TopHat.Compression.CCR.DependencyInjection;
using TopHat.Providers;
using TopHat.Tests.Support;
using Xunit;

namespace TopHat.Tests.Compression.CCR;

/// <summary>Checks bounded CCR history inspection before an HTTP request can reach upstream.</summary>
public sealed class CCRInspectionBoundaryTests
{
	/// <summary>Chunked JSON is repaired without losing bytes from its forward-only source.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task UnknownLengthJson_RepairsHistoryBeforeDispatch(TopHatTarget target)
	{
		var original = History(target);
		JsonObject? received = null;
		var (client, inner, provider) = TransformHandlerFactory.Build(services => services.AddTopHatCCR(),
			configureOptions: options => options.MaxBodyInspectionBytes = 4096,
			behavior: async (request, cancellationToken) =>
			{
				received = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
				return Response();
			});
		using ((IDisposable)provider)
		using (client)
		using (var stream = new TrackingInputStream(Encoding.UTF8.GetBytes(original)))
		using (var request = Request(target, stream, "application/json", knownLength: false))
		{
			Assert.False(stream.CanSeek);
			Assert.Null(request.Content!.Headers.ContentLength);
			using (var response = await client.SendAsync(request))
			{
				Assert.True(response.IsSuccessStatusCode);
				Assert.Single(inner.ReceivedRequests);
				Assert.NotNull(received);
				AssertRepaired(received, target);
				Assert.Contains("preserve user input", received.ToJsonString(), StringComparison.Ordinal);
				Assert.Equal(Encoding.UTF8.GetByteCount(original), stream.BytesRead);
			}
		}
	}

	/// <summary>CCR rejects an uninspectable JSON body before any upstream request or unbounded read.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages, false)]
	[InlineData(TopHatTarget.AnthropicMessages, true)]
	[InlineData(TopHatTarget.OpenAIChatCompletions, false)]
	[InlineData(TopHatTarget.OpenAIChatCompletions, true)]
	[InlineData(TopHatTarget.OpenAIResponses, false)]
	[InlineData(TopHatTarget.OpenAIResponses, true)]
	public async Task OverInspectionCap_RejectsBeforeDispatch(TopHatTarget target, bool knownLength)
	{
		const int cap = 32;
		var payload = Encoding.UTF8.GetBytes(History(target));
		Assert.True(payload.Length > cap);
		var (client, inner, provider) = TransformHandlerFactory.Build(services => services.AddTopHatCCR(),
			configureOptions: options => options.MaxBodyInspectionBytes = cap,
			behavior: (_, _) => Task.FromResult(Response()));
		using ((IDisposable)provider)
		using (client)
		using (var stream = new TrackingInputStream(payload))
		using (var request = Request(target, stream, "application/json", knownLength))
		{
			var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));
			Assert.Contains("MaxBodyInspectionBytes", error.Message, StringComparison.Ordinal);
			Assert.Empty(inner.ReceivedRequests);
			if (knownLength)
			{
				Assert.Equal(0, stream.BytesRead);
			}
			else
			{
				Assert.InRange(stream.BytesRead, 1, cap + 1);
			}
		}
	}

	/// <summary>Cancellation while reading an unknown-length body aborts before upstream dispatch.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task CanceledInspection_PropagatesCancellationBeforeDispatch(TopHatTarget target)
	{
		var (client, inner, provider) = TransformHandlerFactory.Build(services => services.AddTopHatCCR(), behavior: (_, _) => Task.FromResult(Response()));
		using ((IDisposable)provider)
		using (client)
		using (var cancellation = new CancellationTokenSource())
		using (var stream = new TrackingInputStream(Encoding.UTF8.GetBytes(History(target)), cancellation))
		using (var request = Request(target, stream, "application/json", knownLength: false))
		{
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(request, cancellation.Token));
			Assert.True(stream.ReadAttempted);
			Assert.True(cancellation.IsCancellationRequested);
			Assert.Empty(inner.ReceivedRequests);
		}
	}

	/// <summary>Inspection does not reinterpret non-JSON bodies or destroy malformed JSON on a consumed stream.</summary>
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
	public async Task NonJsonOrMalformedJson_PreservesOriginalBody(TopHatTarget target, bool knownLength, bool malformedJson)
	{
		var original = malformedJson ? "  {\"messages\": [ invalid JSON\n" : History(target);
		var mediaType = malformedJson ? "application/json" : "text/plain";
		string? received = null;
		var (client, inner, provider) = TransformHandlerFactory.Build(services => services.AddTopHatCCR(), behavior: async (request, cancellationToken) =>
		{
			received = await request.Content!.ReadAsStringAsync(cancellationToken);
			Assert.Equal(mediaType, request.Content.Headers.ContentType!.MediaType);
			return Response();
		});
		using ((IDisposable)provider)
		using (client)
		using (var stream = new TrackingInputStream(Encoding.UTF8.GetBytes(original)))
		using (var request = Request(target, stream, mediaType, knownLength))
		using (var response = await client.SendAsync(request))
		{
			Assert.True(response.IsSuccessStatusCode);
			Assert.Single(inner.ReceivedRequests);
			Assert.Equal(original, received);
		}
	}

	private static HttpRequestMessage Request(TopHatTarget target, TrackingInputStream stream, string mediaType, bool knownLength)
	{
		var content = new StreamContent(stream);
		content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
		if (knownLength)
		{
			content.Headers.ContentLength = stream.PayloadLength;
		}
		return new HttpRequestMessage(HttpMethod.Post, target switch
		{
			TopHatTarget.AnthropicMessages => "https://api.anthropic.com/v1/messages",
			TopHatTarget.OpenAIChatCompletions => "https://api.openai.com/v1/chat/completions",
			_ => "https://api.openai.com/v1/responses",
		}) { Content = content };
	}

	private static HttpResponseMessage Response() => new(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };

	private static string History(TopHatTarget target) => target switch
	{
		TopHatTarget.AnthropicMessages => """{"model":"test-model","messages":[{"role":"user","content":"preserve user input"},{"role":"assistant","content":[{"type":"tool_use","id":"internal_call","name":"tophat_retrieve","input":{"retrieval_key":"expired"}}]}]}""",
		TopHatTarget.OpenAIChatCompletions => """{"model":"test-model","messages":[{"role":"user","content":"preserve user input"},{"role":"assistant","tool_calls":[{"id":"internal_call","type":"function","function":{"name":"tophat_retrieve","arguments":"{\"retrieval_key\":\"expired\"}"}}]}]}""",
		_ => """{"model":"test-model","input":[{"role":"user","content":"preserve user input"},{"type":"function_call","call_id":"internal_call","name":"tophat_retrieve","arguments":"{\"retrieval_key\":\"expired\"}"}]}""",
	};

	private static void AssertRepaired(JsonObject body, TopHatTarget target)
	{
		var transcript = body[target == TopHatTarget.OpenAIResponses ? "input" : "messages"]!.AsArray();
		Assert.Equal(3, transcript.Count);
		if (target == TopHatTarget.AnthropicMessages)
		{
			Assert.Equal("assistant", transcript[1]!["role"]!.GetValue<string>());
			Assert.Equal("internal_call", transcript[1]!["content"]![0]!["id"]!.GetValue<string>());
			Assert.Equal("user", transcript[2]!["role"]!.GetValue<string>());
			var result = Assert.Single(transcript[2]!["content"]!.AsArray());
			Assert.Equal("tool_result", result!["type"]!.GetValue<string>());
			Assert.Equal("internal_call", result["tool_use_id"]!.GetValue<string>());
		}
		else if (target == TopHatTarget.OpenAIChatCompletions)
		{
			Assert.Equal("assistant", transcript[1]!["role"]!.GetValue<string>());
			Assert.Equal("internal_call", transcript[1]!["tool_calls"]![0]!["id"]!.GetValue<string>());
			Assert.Equal("tool", transcript[2]!["role"]!.GetValue<string>());
			Assert.Equal("internal_call", transcript[2]!["tool_call_id"]!.GetValue<string>());
		}
		else
		{
			Assert.Equal("function_call", transcript[1]!["type"]!.GetValue<string>());
			Assert.Equal("internal_call", transcript[1]!["call_id"]!.GetValue<string>());
			Assert.Equal("function_call_output", transcript[2]!["type"]!.GetValue<string>());
			Assert.Equal("internal_call", transcript[2]!["call_id"]!.GetValue<string>());
		}
	}

	private sealed class TrackingInputStream : Stream
	{
		private readonly MemoryStream _inner;
		private readonly CancellationTokenSource? _cancelOnRead;

		public TrackingInputStream(byte[] payload, CancellationTokenSource? cancelOnRead = null)
		{
			this._inner = new MemoryStream(payload, writable: false);
			this._cancelOnRead = cancelOnRead;
			this.PayloadLength = payload.Length;
		}

		public int PayloadLength { get; }
		public int BytesRead { get; private set; }
		public bool ReadAttempted { get; private set; }
		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			this.BeforeRead(CancellationToken.None);
			var read = this._inner.Read(buffer, offset, count);
			this.BytesRead += read;
			return read;
		}

		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			this.BeforeRead(cancellationToken);
			var read = await this._inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
			this.BytesRead += read;
			return read;
		}

		public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => await this.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

		private void BeforeRead(CancellationToken cancellationToken)
		{
			this.ReadAttempted = true;
			if (this._cancelOnRead is not null)
			{
				this._cancelOnRead.Cancel();
				this._cancelOnRead.Token.ThrowIfCancellationRequested();
			}
			cancellationToken.ThrowIfCancellationRequested();
		}

		public override void Flush() => throw new NotSupportedException();
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				this._inner.Dispose();
			}
			base.Dispose(disposing);
		}
	}
}
