using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using TopHat.Body;
using TopHat.Configuration;
using TopHat.Handlers;
using Xunit;

namespace TopHat.Tests.Body;

public sealed class RequestBodyInspectorTests
{
    [Fact]
    public async Task StreamingTrueBody_PopulatesStreamingFromBodyAndModel()
    {
        (var inspector, var context) = Build();
        using (var req = JsonPost("{\"model\":\"claude-haiku-4-5\",\"stream\":true,\"messages\":[]}"))
        {
            await inspector.InspectAsync(req, context, CancellationToken.None);
        }

        Assert.True(context.StreamingFromBody);
        Assert.Equal("claude-haiku-4-5", context.Model);
    }

    [Fact]
    public async Task StreamingFalseBody_LeavesStreamingFromBodyFalse()
    {
        (var inspector, var context) = Build();
        using (var req = JsonPost("{\"model\":\"claude-haiku-4-5\",\"stream\":false}"))
        {
            await inspector.InspectAsync(req, context, CancellationToken.None);
        }

        Assert.False(context.StreamingFromBody);
        Assert.Equal("claude-haiku-4-5", context.Model);
    }

    [Fact]
    public async Task MissingStreamField_DefaultsFalse()
    {
        (var inspector, var context) = Build();
        using (var req = JsonPost("{\"model\":\"gpt-4o\"}"))
        {
            await inspector.InspectAsync(req, context, CancellationToken.None);
        }

        Assert.False(context.StreamingFromBody);
        Assert.Equal("gpt-4o", context.Model);
    }

    [Fact]
    public async Task MissingModelField_RemainsUnknown()
    {
        (var inspector, var context) = Build();
        using (var req = JsonPost("{\"stream\":true}"))
        {
            await inspector.InspectAsync(req, context, CancellationToken.None);
        }

        Assert.True(context.StreamingFromBody);
        Assert.Equal("unknown", context.Model);
    }

    [Fact]
    public async Task NonJsonContentType_Skipped()
    {
        (var inspector, var context) = Build();
        using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = new StringContent("stream=true&model=x", Encoding.UTF8, "text/plain"),
        })
        {
            await inspector.InspectAsync(req, context, CancellationToken.None);
        }

        Assert.False(context.StreamingFromBody);
        Assert.Equal("unknown", context.Model);
    }

    [Fact]
    public async Task NullContent_Skipped()
    {
        (var inspector, var context) = Build();
        using (var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/messages"))
        {
            await inspector.InspectAsync(req, context, CancellationToken.None);
        }

        Assert.False(context.StreamingFromBody);
        Assert.Equal("unknown", context.Model);
    }

    [Fact]
    public async Task OverCap_SkippedWithoutReading()
    {
        (var inspector, var context) = Build(opts => opts.MaxBodyInspectionBytes = 32);
        var oversized = "{\"model\":\"claude-haiku-4-5\",\"stream\":true,\"padding\":\"" + new string('x', 256) + "\"}";
        using (var req = JsonPost(oversized))
        {
            await inspector.InspectAsync(req, context, CancellationToken.None);
        }

        Assert.False(context.StreamingFromBody);
        Assert.Equal("unknown", context.Model);
    }

    [Fact]
    public async Task UnknownContentLength_Skipped()
    {
        (var inspector, var context) = Build();
        // Non-seekable stream hides length from StreamContent.
        using (var payload = new NonSeekableReadOnlyStream(Encoding.UTF8.GetBytes("{\"stream\":true,\"model\":\"x\"}")))
        {
            var content = new StreamContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages") { Content = content })
            {
                Assert.Null(req.Content!.Headers.ContentLength);

                await inspector.InspectAsync(req, context, CancellationToken.None);
            }
        }

        Assert.False(context.StreamingFromBody);
        Assert.Equal("unknown", context.Model);
    }

    [Fact]
    public async Task MalformedJson_Skipped()
    {
        (var inspector, var context) = Build();
        using (var req = JsonPost("{not json at all"))
        {
            await inspector.InspectAsync(req, context, CancellationToken.None);
        }

        Assert.False(context.StreamingFromBody);
        Assert.Equal("unknown", context.Model);
    }

    [Fact]
    public async Task Inspection_DoesNotConsumeContentForUpstream()
    {
        (var inspector, var context) = Build();
        using (var req = JsonPost("{\"model\":\"x\",\"stream\":true}"))
        {
            await inspector.InspectAsync(req, context, CancellationToken.None);

            // Upstream can still read the full buffered content.
            var bytes = await req.Content!.ReadAsByteArrayAsync();
            var text = Encoding.UTF8.GetString(bytes);
            Assert.Contains("\"stream\":true", text, StringComparison.Ordinal);
        }
    }

    private static (RequestBodyInspector Inspector, TopHatRequestContext Context) Build(Action<TopHatOptions>? configure = null)
    {
        var opts = new TopHatOptions();
        configure?.Invoke(opts);
        var inspector = new RequestBodyInspector(Options.Create(opts), NullLogger.Instance);
        var context = new TopHatRequestContext
        {
            LocalId = "test",
            Stopwatch = Stopwatch.StartNew(),
        };
        return (inspector, context);
    }

    private static HttpRequestMessage JsonPost(string json)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        return req;
    }

    private sealed class NonSeekableReadOnlyStream : Stream
    {
        private readonly MemoryStream _inner;

        public NonSeekableReadOnlyStream(byte[] data)
        {
            this._inner = new MemoryStream(data);
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => this._inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => this._inner.Read(buffer, offset, count);

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
