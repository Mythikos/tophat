using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using TopHat.DependencyInjection;
using TopHat.Providers;
using TopHat.Streaming;
using TopHat.Tests.Support;
using TopHat.Transforms;
using Xunit;

namespace TopHat.Tests.Transforms;

/// <summary>
/// Tests for the M5 response-transform pipeline: DI registration, filter/order/failure-mode
/// semantics, TeeMode-aware context population, sync-vs-async disposal dispatch behavior.
/// </summary>
public sealed class ResponseTransformPipelineTests
{
    [Fact]
    public void AddTopHatResponseTransform_RegistersWithResponseKind()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTopHat();
        services.AddTopHatResponseTransform<RecordingResponseTransform>(o => o.WithOrder(42));

        var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<TopHatTransformRegistry>();

        var entry = Assert.Single(registry.Registrations);
        Assert.Equal(typeof(RecordingResponseTransform), entry.TransformType);
        Assert.Equal(42, entry.Order);
    }

    [Fact]
    public async Task WholeBodyJsonResponse_PopulatesBody_AndInvokesResponseTransform()
    {
        var record = new ResponseRecord();
        (var client, var _, var _) = TransformHandlerFactory.Build(
            s =>
            {
                s.AddSingleton(record);
                s.AddTopHatResponseTransform<RecordingResponseTransform>();
            },
            behavior: (_, _) =>
            {
                var content = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"ok\":true,\"n\":7}"));
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

        var obs = Assert.Single(record.Observations);
        Assert.Equal(TeeMode.WholeBody, obs.Mode);
        Assert.Equal(200, obs.StatusCode);
        Assert.Equal(TopHatTarget.AnthropicMessages, obs.Target);
        Assert.NotNull(obs.Body);
        Assert.Equal(7, obs.Body!["n"]!.GetValue<int>());
    }

    [Fact]
    public async Task SseResponse_PopulatesObservedEvents_UsageBearingOnly()
    {
        var record = new ResponseRecord();
        (var client, var _, var _) = TransformHandlerFactory.Build(
            s =>
            {
                s.AddSingleton(record);
                s.AddTopHatResponseTransform<RecordingResponseTransform>();
            },
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
                _ = await response.Content.ReadAsByteArrayAsync();
            }
        }

        var obs = Assert.Single(record.Observations);
        Assert.Equal(TeeMode.Sse, obs.Mode);
        Assert.Null(obs.Body);
        Assert.NotNull(obs.ObservedEvents);
        // Anthropic fixture has message_start + message_delta → 2 usage-bearing frames.
        Assert.Equal(2, obs.ObservedEvents!.Count);
        Assert.All(obs.ObservedEvents, e => Assert.NotNull(e.UsageFrame));
        // Total frames observed >= 2 (non-usage frames are counted but not retained).
        Assert.True(obs.ObservedEventCount >= 2);
        Assert.False(obs.TruncatedObservedEvents);
    }

    [Fact]
    public async Task PassthroughResponse_StillDispatches_WithNullBodyAndEvents()
    {
        var record = new ResponseRecord();
        (var client, var _, var _) = TransformHandlerFactory.Build(
            s =>
            {
                s.AddSingleton(record);
                s.AddTopHatResponseTransform<RecordingResponseTransform>();
            },
            behavior: (_, _) =>
            {
                var content = new ByteArrayContent(Encoding.UTF8.GetBytes("OK"));
                content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            });

        using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = new StringContent("{\"model\":\"x\"}", Encoding.UTF8, "application/json"),
        })
        {
            using (var response = await client.SendAsync(req))
            {
                _ = await response.Content.ReadAsByteArrayAsync();
            }
        }

        var obs = Assert.Single(record.Observations);
        Assert.Equal(TeeMode.Passthrough, obs.Mode);
        Assert.Null(obs.Body);
        Assert.Null(obs.ObservedEvents);
    }

    [Fact]
    public async Task FailOpenTransform_ExceptionSwallowed_AndErrorCounterIncremented()
    {
        using (var capture = new MetricsCapture())
        {
            var record = new ResponseRecord();
            (var client, var _, var _) = TransformHandlerFactory.Build(
                s =>
                {
                    s.AddSingleton(record);
                    s.AddTopHatResponseTransform<ThrowingResponseTransform>();  // FailOpen default
                    s.AddTopHatResponseTransform<RecordingResponseTransform>();  // must still run
                },
                behavior: (_, _) =>
                {
                    var c = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"ok\":true}"));
                    c.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = c });
                });

            using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            {
                Content = new StringContent("{\"model\":\"x\"}", Encoding.UTF8, "application/json"),
            })
            {
                using (var response = await client.SendAsync(req))
                {
                    _ = await response.Content.ReadAsByteArrayAsync();
                }
            }

            Assert.Single(record.Observations);  // Recording ran after Throwing failed.
            var errors = capture.ForInstrument("tophat.transform.errors").ToList();
            Assert.Contains(errors, r => Equals(r.Tag("transform_name"), nameof(ThrowingResponseTransform)) && Equals(r.Tag("phase"), "response"));
        }
    }

    [Fact]
    public async Task FilterPredicateThrows_TreatedAsFalse_AndFilterErrorCounterIncremented()
    {
        using (var capture = new MetricsCapture())
        {
            var record = new ResponseRecord();
            (var client, var _, var _) = TransformHandlerFactory.Build(
                s =>
                {
                    s.AddSingleton(record);
                    s.AddTopHatResponseTransform<RecordingResponseTransform>(o => o.AppliesTo(_ => throw new InvalidOperationException("boom")));
                },
                behavior: (_, _) =>
                {
                    var c = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"ok\":true}"));
                    c.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = c });
                });

            using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            {
                Content = new StringContent("{\"model\":\"x\"}", Encoding.UTF8, "application/json"),
            })
            {
                using (var response = await client.SendAsync(req))
                {
                    _ = await response.Content.ReadAsByteArrayAsync();
                }
            }

            Assert.Empty(record.Observations);  // filter threw → transform not invoked
            var filterErrors = capture.ForInstrument("tophat.transform.errors")
                .Where(r => Equals(r.Tag("kind"), "filter") && Equals(r.Tag("phase"), "response"))
                .ToList();
            Assert.NotEmpty(filterErrors);
        }
    }

    [Fact]
    public async Task TargetFilter_OnlyInvokesForMatchingTarget()
    {
        var record = new ResponseRecord();
        (var client, var _, var _) = TransformHandlerFactory.Build(
            s =>
            {
                s.AddSingleton(record);
                s.AddTopHatResponseTransform<RecordingResponseTransform>(o => o.AppliesTo(TopHatTarget.OpenAIChatCompletions));
            },
            behavior: (_, _) =>
            {
                var c = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"ok\":true}"));
                c.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = c });
            });

        using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = new StringContent("{\"model\":\"x\"}", Encoding.UTF8, "application/json"),
        })
        {
            using (var response = await client.SendAsync(req))
            {
                _ = await response.Content.ReadAsByteArrayAsync();
            }
        }

        Assert.Empty(record.Observations);
    }

    [Fact]
    public async Task Ordering_HonoredAcrossTransforms()
    {
        var log = new List<string>();
        (var client, var _, var _) = TransformHandlerFactory.Build(
            s =>
            {
                s.AddSingleton(log);
                s.AddTopHatResponseTransform<OrderBTransform>(o => o.WithOrder(20));
                s.AddTopHatResponseTransform<OrderATransform>(o => o.WithOrder(10));
            },
            behavior: (_, _) =>
            {
                var c = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"ok\":true}"));
                c.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = c });
            });

        using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = new StringContent("{\"model\":\"x\"}", Encoding.UTF8, "application/json"),
        })
        {
            using (var response = await client.SendAsync(req))
            {
                _ = await response.Content.ReadAsByteArrayAsync();
            }
        }

        Assert.Equal(new[] { "A", "B" }, log.ToArray());
    }

    [Fact]
    public async Task SyncDisposal_SkipsResponseTransformDispatch()
    {
        var record = new ResponseRecord();
        (var client, var _, var _) = TransformHandlerFactory.Build(
            s =>
            {
                s.AddSingleton(record);
                s.AddTopHatResponseTransform<RecordingResponseTransform>();
            },
            behavior: (_, _) =>
            {
                var c = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"ok\":true}"));
                c.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = c });
            });

        using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = new StringContent("{\"model\":\"x\"}", Encoding.UTF8, "application/json"),
        })
        {
            using (var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
            {
                // Read via a sync stream and sync-dispose.
                var stream = await response.Content.ReadAsStreamAsync();
                // Drain and then sync-dispose the stream and response.
                using (stream)
                {
                    var buf = new byte[16];
                    while (stream.Read(buf, 0, buf.Length) > 0)
                    {
                        // drain
                    }
                }
            }
        }

        Assert.Empty(record.Observations);
    }

    [Fact]
    public async Task AsyncDisposal_DispatchesResponseTransforms()
    {
        var record = new ResponseRecord();
        (var client, var _, var _) = TransformHandlerFactory.Build(
            s =>
            {
                s.AddSingleton(record);
                s.AddTopHatResponseTransform<RecordingResponseTransform>();
            },
            behavior: (_, _) =>
            {
                var c = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"ok\":true}"));
                c.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = c });
            });

        using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = new StringContent("{\"model\":\"x\"}", Encoding.UTF8, "application/json"),
        })
        {
            using (var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
            {
                // ReadAsByteArrayAsync internally fully reads to EOF, triggering async finalization.
                _ = await response.Content.ReadAsByteArrayAsync();
            }
        }

        Assert.Single(record.Observations);
    }

    internal sealed class ResponseRecord
    {
        public ConcurrentQueue<Observation> Queue { get; } = new();

        public IReadOnlyList<Observation> Observations => this.Queue.ToArray();
    }

    internal sealed record Observation(
        TopHatTarget Target,
        int StatusCode,
        TeeMode Mode,
        System.Text.Json.Nodes.JsonNode? Body,
        IReadOnlyList<SseObservation>? ObservedEvents,
        int ObservedEventCount,
        bool TruncatedObservedEvents,
        string LocalId);

    internal sealed class RecordingResponseTransform : IResponseTransform
    {
        private readonly ResponseRecord _record;

        public RecordingResponseTransform(ResponseRecord record)
        {
            this._record = record;
        }

        public ValueTask InvokeAsync(ResponseTransformContext context, CancellationToken cancellationToken)
        {
            this._record.Queue.Enqueue(new Observation(
                context.Target,
                context.StatusCode,
                context.Mode,
                context.Body,
                context.ObservedEvents,
                context.ObservedEventCount,
                context.TruncatedObservedEvents,
                context.LocalId));
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class ThrowingResponseTransform : IResponseTransform
    {
        public ValueTask InvokeAsync(ResponseTransformContext context, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("deliberate failure for fail-open test");
        }
    }

    internal sealed class OrderATransform : IResponseTransform
    {
        private readonly List<string> _log;

        public OrderATransform(List<string> log)
        {
            this._log = log;
        }

        public ValueTask InvokeAsync(ResponseTransformContext context, CancellationToken cancellationToken)
        {
            lock (this._log)
            {
                this._log.Add("A");
            }

            return ValueTask.CompletedTask;
        }
    }

    internal sealed class OrderBTransform : IResponseTransform
    {
        private readonly List<string> _log;

        public OrderBTransform(List<string> log)
        {
            this._log = log;
        }

        public ValueTask InvokeAsync(ResponseTransformContext context, CancellationToken cancellationToken)
        {
            lock (this._log)
            {
                this._log.Add("B");
            }

            return ValueTask.CompletedTask;
        }
    }
}
