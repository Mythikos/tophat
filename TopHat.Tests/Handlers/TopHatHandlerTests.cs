using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using TopHat.Handlers;
using TopHat.Tests.Support;
using Xunit;

namespace TopHat.Tests.Handlers;

public sealed class TopHatHandlerTests
{
    [Fact]
    public async Task NoRewrite_WhenOptionsAreNull()
    {
        (var client, var inner, var _) = HandlerFactory.Build();

        using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages?beta=foo"))
        {
            using (var response = await client.SendAsync(req))
            {
                var observed = Assert.Single(inner.ReceivedRequests).RequestUri;
                Assert.Equal("https://api.anthropic.com/v1/messages?beta=foo", observed!.ToString());
            }
        }
    }

    [Fact]
    public async Task AnthropicUriIsRewritten_PreservingPathAndQuery()
    {
        (var client, var inner, var _) = HandlerFactory.Build(o => o.AnthropicBaseUrl = new Uri("https://gateway.internal:9000"));

        using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages?beta=foo&cache=1"))
        {
            using (var response = await client.SendAsync(req))
            {
                var observed = Assert.Single(inner.ReceivedRequests).RequestUri;
                Assert.Equal("https://gateway.internal:9000/v1/messages?beta=foo&cache=1", observed!.ToString());
            }
        }
    }

    [Fact]
    public async Task OpenAiUriIsRewritten_PreservingPathAndQuery()
    {
        (var client, var inner, var _) = HandlerFactory.Build(o => o.OpenAiBaseUrl = new Uri("https://openai.internal"));

        using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions?param=x"))
        {
            using (var response = await client.SendAsync(req))
            {
                var observed = Assert.Single(inner.ReceivedRequests).RequestUri;
                Assert.Equal("https://openai.internal/v1/chat/completions?param=x", observed!.ToString());
            }
        }
    }

    [Fact]
    public async Task RewriteAppliesToUnknownTargetsOnKnownHost()
    {
        (var client, var inner, var _) = HandlerFactory.Build(o => o.AnthropicBaseUrl = new Uri("https://gateway.internal:9000"));

        using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/some-new-endpoint"))
        {
            using (var response = await client.SendAsync(req))
            {
                var observed = Assert.Single(inner.ReceivedRequests).RequestUri;
                Assert.Equal("https://gateway.internal:9000/v1/some-new-endpoint", observed!.ToString());
            }
        }
    }

    [Fact]
    public async Task NoRewriteWhenHostIsUnrelated()
    {
        (var client, var inner, var _) = HandlerFactory.Build(o =>
        {
            o.AnthropicBaseUrl = new Uri("https://gateway.internal");
            o.OpenAiBaseUrl = new Uri("https://openai.internal");
        });

        using (var req = new HttpRequestMessage(HttpMethod.Post, "https://example.com/v1/messages"))
        {
            using (var response = await client.SendAsync(req))
            {
                var observed = Assert.Single(inner.ReceivedRequests).RequestUri;
                Assert.Equal("https://example.com/v1/messages", observed!.ToString());
            }
        }
    }

    [Fact]
    public async Task BypassViaHeader_StripsHeaderBeforeForwarding()
    {
        (var client, var inner, var _) = HandlerFactory.Build(o => o.AnthropicBaseUrl = new Uri("https://gateway.internal"));

        using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages"))
        {
            req.Headers.Add("x-tophat-bypass", "true");

            using (var response = await client.SendAsync(req))
            {
                var observed = Assert.Single(inner.ReceivedRequests);
                Assert.False(observed.Headers.Contains("x-tophat-bypass"), "bypass header must not leak to upstream");
                // Bypass also skips rewrite
                Assert.Equal("https://api.anthropic.com/v1/messages", observed.RequestUri!.ToString());
            }
        }
    }

    [Fact]
    public async Task BypassViaRequestOptions_SkipsRewrite()
    {
        (var client, var inner, var _) = HandlerFactory.Build(o => o.AnthropicBaseUrl = new Uri("https://gateway.internal"));

        using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages"))
        {
            req.Options.Set(new HttpRequestOptionsKey<bool>(TopHatHandler.BypassOptionsKey), true);

            using (var response = await client.SendAsync(req))
            {
                var observed = Assert.Single(inner.ReceivedRequests);
                Assert.Equal("https://api.anthropic.com/v1/messages", observed.RequestUri!.ToString());
            }
        }
    }

    [Fact]
    public async Task StreamingResponseFlowsWithoutBuffering()
    {
        // BlockingPipeStream blocks on ReadAsync until explicitly signaled, proving the handler
        // does not eagerly drain the response body.
        using (var streamSource = new BlockingPipeStream())
        {
            var responseContent = new StreamContent(streamSource);
            responseContent.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");

            (var client, var _, var _) = HandlerFactory.Build(
                behavior: (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = responseContent }));

            using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages"))
            {
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

                // If the handler buffered the response, SendAsync would hang here indefinitely.
                using (var sendCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                {
                    using (var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, sendCts.Token))
                    {
                        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                        // Now push bytes through the pipe and confirm they arrive at the caller.
                        var payload1 = "event: message_start\n\n"u8.ToArray();
                        var payload2 = "event: message_stop\n\n"u8.ToArray();

                        await using (var responseStream = await response.Content.ReadAsStreamAsync(sendCts.Token))
                        {
                            var buffer = new byte[256];

                            var read1Task = responseStream.ReadAsync(buffer.AsMemory(), sendCts.Token);
                            await streamSource.SignalAsync(payload1);
                            var read1 = await read1Task;
                            Assert.Equal(payload1.Length, read1);

                            var read2Task = responseStream.ReadAsync(buffer.AsMemory(), sendCts.Token);
                            await streamSource.SignalAsync(payload2);
                            var read2 = await read2Task;
                            Assert.Equal(payload2.Length, read2);

                            await streamSource.CompleteAsync();
                            var read3 = await responseStream.ReadAsync(buffer.AsMemory(), sendCts.Token);
                            Assert.Equal(0, read3);
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public async Task UpstreamExceptionPropagates_AndRecordsErrorMetric()
    {
        using (var capture = new MetricsCapture())
        {
            (var client, var _, var _) = HandlerFactory.Build(
                behavior: (_, _) => Task.FromException<HttpResponseMessage>(
                    new HttpRequestException("connection refused", new SocketException(10061))));

            using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages"))
            {
                await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(req));
            }

            var errors = capture.ForInstrument("tophat.upstream.errors").ToList();
            var recording = Assert.Single(errors);
            Assert.Equal("connection", recording.Tag("kind"));
        }
    }

    [Fact]
    public async Task Metrics_RecordedOnSuccess()
    {
        using (var capture = new MetricsCapture())
        {
            (var client, var _, var _) = HandlerFactory.Build(
                behavior: (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

            using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages"))
            {
                using (var response = await client.SendAsync(req))
                {
                    var requestsCount = capture.ForInstrument("tophat.requests").ToList();
                    Assert.Single(requestsCount);
                    Assert.Equal("AnthropicMessages", requestsCount[0].Tag("target"));
                    Assert.Equal(200, requestsCount[0].Tag("status_code"));
                    Assert.Equal("false", requestsCount[0].Tag("bypass"));

                    Assert.NotEmpty(capture.ForInstrument("tophat.request.ttfb"));
                }
            }
        }
    }

    [Fact]
    public async Task Metrics_BypassRecordsBypassTag()
    {
        using (var capture = new MetricsCapture())
        {
            (var client, var _, var _) = HandlerFactory.Build();

            using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages"))
            {
                req.Options.Set(new HttpRequestOptionsKey<bool>(TopHatHandler.BypassOptionsKey), true);
                using (var response = await client.SendAsync(req))
                {
                    var bypassed = capture.ForInstrument("tophat.requests").Single();
                    Assert.Equal("bypassed", bypassed.Tag("target"));
                    Assert.Equal("true", bypassed.Tag("bypass"));
                }
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StreamingTag_ReflectsAcceptHeader(bool withSseAccept)
    {
        using (var capture = new MetricsCapture())
        {
            (var client, var _, var _) = HandlerFactory.Build(
                behavior: (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

            using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages"))
            {
                if (withSseAccept)
                {
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                }

                using (var response = await client.SendAsync(req))
                {
                    var recording = capture.ForInstrument("tophat.requests").Single();
                    Assert.Equal(withSseAccept ? "true" : "false", recording.Tag("streaming"));
                }
            }
        }
    }
}
