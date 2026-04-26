using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Net.Http.Headers;
using TopHat.Configuration;
using TopHat.Handlers;
using TopHat.Providers;
using TopHat.Streaming;
using TopHat.Tests.Support;
using Xunit;

namespace TopHat.Tests.Streaming;

public sealed class TeeStreamTests
{
    [Fact]
    public async Task PassthroughMode_ForwardsBytesVerbatim()
    {
        var payload = "hello world"u8.ToArray();
        (var tee, var _, var _) = Build(payload, TeeMode.Passthrough);

        using (var destination = new MemoryStream())
        {
            await tee.CopyToAsync(destination);
            Assert.Equal(payload, destination.ToArray());
        }
    }

    [Fact]
    public async Task EofRecordsOutcome_AndDuration()
    {
        using (var capture = new MetricsCapture())
        {
            (var tee, var context, var _) = Build("hi"u8.ToArray(), TeeMode.Passthrough);

            using (var destination = new MemoryStream())
            {
                await tee.CopyToAsync(destination);
            }

            var outcomes = capture.ForInstrument("tophat.stream.outcome").ToList();
            Assert.Single(outcomes);
            Assert.Equal("eof", outcomes[0].Tag("outcome"));
            Assert.Equal(StreamOutcome.Eof, context.Outcome);

            Assert.NotEmpty(capture.ForInstrument("tophat.request.duration"));
        }
    }

    [Fact]
    public async Task EofFollowedByDispose_RecordsOutcomeOnce()
    {
        using (var capture = new MetricsCapture())
        {
            (var tee, var _, var _) = Build("hi"u8.ToArray(), TeeMode.Passthrough);

            using (var destination = new MemoryStream())
            {
                await tee.CopyToAsync(destination);
            }

            await tee.DisposeAsync();

            Assert.Single(capture.ForInstrument("tophat.stream.outcome"));
        }
    }

    [Fact]
    public async Task DisposeWithoutEof_RecordsDisposedOutcome()
    {
        using (var capture = new MetricsCapture())
        {
            (var tee, var context, var _) = Build(new byte[1024], TeeMode.Passthrough);

            // Read a few bytes, then dispose without reaching EOF.
            var buffer = new byte[4];
            _ = await tee.ReadAsync(buffer);
            await tee.DisposeAsync();

            var outcome = Assert.Single(capture.ForInstrument("tophat.stream.outcome"));
            Assert.Equal("disposed", outcome.Tag("outcome"));
            Assert.Equal(StreamOutcome.Disposed, context.Outcome);
        }
    }

    [Fact]
    public async Task UpstreamException_RecordsErrorOutcome_Rethrows()
    {
        using (var capture = new MetricsCapture())
        {
            var failing = new ThrowingStream();
            (var tee, var context) = BuildWithInner(failing, TeeMode.Passthrough);

            var buffer = new byte[4];
            await Assert.ThrowsAsync<IOException>(async () => await tee.ReadAsync(buffer));

            var outcome = Assert.Single(capture.ForInstrument("tophat.stream.outcome"));
            Assert.Equal("error", outcome.Tag("outcome"));
            Assert.Equal(StreamOutcome.Error, context.Outcome);
        }
    }

    [Fact]
    public async Task SseMode_PopulatesTokenCounters()
    {
        using (var capture = new MetricsCapture())
        {
            (var tee, var _, var _) = Build(SseFixtures.AnthropicStreamBytes, TeeMode.Sse, TopHatProviderKind.Anthropic, "claude-haiku-4-5");

            using (var destination = new MemoryStream())
            {
                await tee.CopyToAsync(destination);
                Assert.Equal(SseFixtures.AnthropicStreamBytes, destination.ToArray());  // passthrough verbatim
            }

            var input = Sum(capture, "tophat.tokens.input");
            var output = Sum(capture, "tophat.tokens.output");
            Assert.Equal(42, input);
            Assert.Equal(17, output);
        }
    }

    [Fact]
    public async Task WholeBodyMode_PopulatesAtEof()
    {
        using (var capture = new MetricsCapture())
        {
            (var tee, var _, var _) = Build(SseFixtures.AnthropicNonStreamingBytes, TeeMode.WholeBody, TopHatProviderKind.Anthropic, "claude-haiku-4-5");

            using (var destination = new MemoryStream())
            {
                await tee.CopyToAsync(destination);
            }

            Assert.Equal(42, Sum(capture, "tophat.tokens.input"));
            Assert.Equal(5, Sum(capture, "tophat.tokens.output"));
        }
    }

    [Fact]
    public async Task WholeBodyMode_OverCap_ForwardsButSkipsExtraction_RecordsFramingError()
    {
        using (var capture = new MetricsCapture())
        {
            var payload = new byte[4096];
            Array.Fill(payload, (byte)'x');
            (var tee, var _, var _) = Build(payload, TeeMode.WholeBody, TopHatProviderKind.Anthropic, "x", configure: o => o.MaxResponseInspectionBytes = 64);

            using (var destination = new MemoryStream())
            {
                await tee.CopyToAsync(destination);
                Assert.Equal(payload.Length, destination.Length);  // consumer got all bytes
            }

            Assert.Equal(0, Sum(capture, "tophat.tokens.input"));  // no extraction

            var framing = capture.ForInstrument("tophat.parser.errors").ToList();
            Assert.Contains(framing, r => (string?)r.Tag("kind") == "framing");
        }
    }

    private static (TeeStream Tee, TopHatRequestContext Context, MemoryStream Inner) Build(
        byte[] payload,
        TeeMode mode,
        TopHatProviderKind provider = TopHatProviderKind.Other,
        string model = "unknown",
        Action<TopHatOptions>? configure = null)
    {
        var inner = new MemoryStream(payload);
        (var tee, var context) = BuildWithInner(inner, mode, provider, model, configure);
        return (tee, context, inner);
    }

    private static (TeeStream Tee, TopHatRequestContext Context) BuildWithInner(
        Stream inner,
        TeeMode mode,
        TopHatProviderKind provider = TopHatProviderKind.Other,
        string model = "unknown",
        Action<TopHatOptions>? configure = null)
    {
        var opts = new TopHatOptions();
        configure?.Invoke(opts);
        var context = new TopHatRequestContext
        {
            LocalId = "test",
            Stopwatch = Stopwatch.StartNew(),
            Provider = provider,
            Target = provider == TopHatProviderKind.Anthropic ? TopHatTarget.AnthropicMessages :
                     provider == TopHatProviderKind.OpenAI ? TopHatTarget.OpenAIChatCompletions :
                     TopHatTarget.Unknown,
            Model = model,
        };

        var recorder = provider != TopHatProviderKind.Other
            ? new UsageRecorder(provider, context.Target.ToString(), model)
            : null;

        var tee = new TeeStream(inner, mode, context, 200, recorder, Options.Create(opts), NullLogger.Instance);
        return (tee, context);
    }

    private static double Sum(MetricsCapture capture, string name)
    {
        var total = 0d;
        foreach (var r in capture.ForInstrument(name))
        {
            total += r.Value;
        }

        return total;
    }

    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("boom");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("boom"));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class ObservingHttpContentTests
{
    [Fact]
    public async Task HeaderFidelity_ContentTypeAndEncoding()
    {
        var inner = new ByteArrayContent(SseFixtures.AnthropicStreamBytes);
        inner.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        inner.Headers.ContentEncoding.Add("identity");

        var context = new TopHatRequestContext
        {
            LocalId = "t",
            Stopwatch = Stopwatch.StartNew(),
            Provider = TopHatProviderKind.Anthropic,
            Target = TopHatTarget.AnthropicMessages,
            Model = "x",
        };
        var recorder = new UsageRecorder(TopHatProviderKind.Anthropic, "AnthropicMessages", "x");
        using (var wrapped = new ObservingHttpContent(
            inner,
            context,
            200,
            recorder,
            Options.Create(new TopHatOptions()),
            NullLogger.Instance))
        {
            Assert.Equal("text/event-stream", wrapped.Headers.ContentType?.MediaType);
            Assert.Contains("identity", wrapped.Headers.ContentEncoding);

            // Reading the wrapped stream yields the original bytes verbatim.
            var roundTripped = await wrapped.ReadAsByteArrayAsync();
            Assert.Equal(SseFixtures.AnthropicStreamBytes, roundTripped);
        }
    }
}
