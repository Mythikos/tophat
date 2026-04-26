using System.Net;
using System.Net.Http.Headers;
using System.Text;
using TopHat.Tests.Support;
using Xunit;

namespace TopHat.Tests.Handlers;

public sealed class TopHatHandlerStreamingTests
{
    [Fact]
    public async Task StreamingAnthropic_PopulatesTokenMetricsAndDuration()
    {
        using (var capture = new MetricsCapture())
        {
            (var client, var _, var _) = HandlerFactory.Build(
                behavior: (_, _) =>
                {
                    var content = new ByteArrayContent(SseFixtures.AnthropicStreamBytes);
                    content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
                });

            using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            {
                Content = new StringContent("{\"model\":\"claude-haiku-4-5\",\"stream\":true,\"messages\":[]}", Encoding.UTF8, "application/json"),
            })
            {
                using (var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
                {
                    var body = await response.Content.ReadAsByteArrayAsync();
                    Assert.Equal(SseFixtures.AnthropicStreamBytes, body);
                }
            }

            Assert.Equal(42, Sum(capture, "tophat.tokens.input"));
            Assert.Equal(17, Sum(capture, "tophat.tokens.output"));
            Assert.Equal(25, Sum(capture, "tophat.tokens.cache_read"));
            Assert.Equal(100, Sum(capture, "tophat.tokens.cache_creation"));

            Assert.NotEmpty(capture.ForInstrument("tophat.request.duration"));
            Assert.NotEmpty(capture.ForInstrument("tophat.request.ttfb"));

            var requests = capture.ForInstrument("tophat.requests").Single();
            Assert.Equal("true", requests.Tag("streaming"));  // body-authoritative
            Assert.Equal("claude-haiku-4-5", requests.Tag("model"));
        }
    }

    [Fact]
    public async Task NonStreamingAnthropic_ExtractsUsageAtEof()
    {
        using (var capture = new MetricsCapture())
        {
            (var client, var _, var _) = HandlerFactory.Build(
                behavior: (_, _) =>
                {
                    var content = new ByteArrayContent(SseFixtures.AnthropicNonStreamingBytes);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
                });

            using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            {
                Content = new StringContent("{\"model\":\"claude-haiku-4-5\",\"messages\":[]}", Encoding.UTF8, "application/json"),
            })
            {
                using (var response = await client.SendAsync(req))
                {
                    _ = await response.Content.ReadAsByteArrayAsync();
                }
            }

            Assert.Equal(42, Sum(capture, "tophat.tokens.input"));
            Assert.Equal(5, Sum(capture, "tophat.tokens.output"));
        }
    }

    [Fact]
    public async Task StreamingTagFromBody_EvenWithoutAcceptHeader()
    {
        using (var capture = new MetricsCapture())
        {
            (var client, var _, var _) = HandlerFactory.Build(
                behavior: (_, _) =>
                {
                    var content = new ByteArrayContent(SseFixtures.AnthropicStreamBytes);
                    content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
                });

            using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            {
                Content = new StringContent("{\"model\":\"claude-haiku-4-5\",\"stream\":true}", Encoding.UTF8, "application/json"),
            })
            {
                // Note: no Accept: text/event-stream header.
                using (var response = await client.SendAsync(req))
                {
                    _ = await response.Content.ReadAsByteArrayAsync();
                }
            }

            var requests = capture.ForInstrument("tophat.requests").Single();
            Assert.Equal("true", requests.Tag("streaming"));
        }
    }

    [Fact]
    public async Task DisposeAfterPartialRead_RecordsDurationAndDisposedOutcome()
    {
        using (var capture = new MetricsCapture())
        {
            (var client, var _, var _) = HandlerFactory.Build(
                behavior: (_, _) =>
                {
                    var content = new ByteArrayContent(SseFixtures.AnthropicStreamBytes);
                    content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
                });

            using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            {
                Content = new StringContent("{\"stream\":true,\"model\":\"x\"}", Encoding.UTF8, "application/json"),
            })
            {
                var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

                // Open the stream and read a few bytes so the tee is actually constructed.
                await using (var stream = await response.Content.ReadAsStreamAsync())
                {
                    var buffer = new byte[4];
                    _ = await stream.ReadAsync(buffer);
                }

                // Stream dispose happens on the using above; then dispose the response itself.
                response.Dispose();
            }

            Assert.NotEmpty(capture.ForInstrument("tophat.request.duration"));
            var outcomes = capture.ForInstrument("tophat.stream.outcome").ToList();
            Assert.Single(outcomes);
            Assert.Equal("disposed", outcomes[0].Tag("outcome"));
        }
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
}
