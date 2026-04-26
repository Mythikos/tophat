using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TopHat.DependencyInjection;
using TopHat.Handlers;
using TopHat.Providers;
using TopHat.Tests.Support;
using TopHat.Transforms;
using Xunit;

namespace TopHat.Tests.Transforms;

public sealed class TransformPipelineTests
{
    [Fact]
    public async Task NoMutatingTransforms_ContentReferenceUnchanged()
    {
        HttpContent? capturedContent = null;
        (var client, var _, var _) = TransformHandlerFactory.Build(
            s => s.AddTopHatRequestTransform<NoOpRequestTransform>(),
            behavior: (req, _) =>
            {
                capturedContent = req.Content;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            });

        using (var req = JsonPost("{\"model\":\"x\",\"stream\":false}"))
        {
            var originalContent = req.Content!;
            using (var response = await client.SendAsync(req))
            {
                Assert.Same(originalContent, capturedContent);
            }
        }
    }

    [Fact]
    public async Task MutatingTransform_ReplacesContent_AndUpstreamSeesPostTransformBytes()
    {
        byte[]? capturedBytes = null;
        (var client, var _, var _) = TransformHandlerFactory.Build(
            s => s.AddTopHatRequestTransform<ExampleMetadataStripTransform>(),
            behavior: async (req, _) =>
            {
                capturedBytes = await req.Content!.ReadAsByteArrayAsync();
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        using (var req = JsonPost("{\"model\":\"x\",\"stream\":false,\"metadata\":{\"trace\":\"abc\"}}"))
        {
            using (var response = await client.SendAsync(req))
            {
                Assert.NotNull(capturedBytes);
                var text = Encoding.UTF8.GetString(capturedBytes);
                Assert.DoesNotContain("metadata", text, StringComparison.Ordinal);
                Assert.Contains("\"model\":\"x\"", text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task MetricsFireForInvocationAndMutation()
    {
        using (var capture = new MetricsCapture())
        {
            (var client, var _, var _) = TransformHandlerFactory.Build(
                s =>
                {
                    s.AddTopHatRequestTransform<NoOpRequestTransform>();
                    s.AddTopHatRequestTransform<ExampleMetadataStripTransform>();
                });

            using (var req = JsonPost("{\"model\":\"x\",\"metadata\":{\"a\":1}}"))
            {
                using (var response = await client.SendAsync(req))
                {
                }
            }

            var invoked = capture.ForInstrument("tophat.transform.invoked").ToList();
            Assert.Equal(2, invoked.Count);

            var mutated = capture.ForInstrument("tophat.transform.mutated").ToList();
            var mutation = Assert.Single(mutated);
            Assert.Equal("ExampleMetadataStripTransform", mutation.Tag("transform_name"));
        }
    }

    [Fact]
    public async Task NoMutation_TransformMutatedCounterDoesNotFire()
    {
        using (var capture = new MetricsCapture())
        {
            (var client, var _, var _) = TransformHandlerFactory.Build(
                s => s.AddTopHatRequestTransform<ExampleMetadataStripTransform>());

            // Body has no `metadata` field — the transform inspects and no-ops.
            using (var req = JsonPost("{\"model\":\"x\",\"stream\":false}"))
            {
                using (var response = await client.SendAsync(req))
                {
                }
            }

            Assert.Empty(capture.ForInstrument("tophat.transform.mutated"));
            Assert.Single(capture.ForInstrument("tophat.transform.invoked"));
        }
    }

    [Fact]
    public async Task FilterByTarget_SkipsNonMatching()
    {
        using (var capture = new MetricsCapture())
        {
            (var client, var _, var _) = TransformHandlerFactory.Build(
                s => s.AddTopHatRequestTransform<NoOpRequestTransform>(
                    cfg => cfg.AppliesTo(TopHatTarget.OpenAIChatCompletions)));

            // Anthropic target — filter doesn't match.
            using (var req = JsonPost("{\"model\":\"x\"}"))
            {
                using (var response = await client.SendAsync(req))
                {
                }
            }

            Assert.Empty(capture.ForInstrument("tophat.transform.invoked"));
        }
    }

    [Fact]
    public async Task FilterByPredicate_AppliesCustomLogic()
    {
        using (var capture = new MetricsCapture())
        {
            (var client, var _, var _) = TransformHandlerFactory.Build(
                s => s.AddTopHatRequestTransform<NoOpRequestTransform>(
                    cfg => cfg.AppliesTo(ctx => ctx.Model.StartsWith("claude", StringComparison.OrdinalIgnoreCase))));

            using (var req = JsonPost("{\"model\":\"gpt-4o\"}"))
            {
                using (var response = await client.SendAsync(req))
                {
                }
            }

            Assert.Empty(capture.ForInstrument("tophat.transform.invoked"));

            using (var req2 = JsonPost("{\"model\":\"claude-haiku-4-5\"}"))
            {
                using (var response = await client.SendAsync(req2))
                {
                }
            }

            Assert.Single(capture.ForInstrument("tophat.transform.invoked"));
        }
    }

    [Fact]
    public async Task RegistrationOrderRespected()
    {
        using (var capture = new MetricsCapture())
        {
            var observed = new List<string>();
            (var client, var _, var _) = TransformHandlerFactory.Build(
                s =>
                {
                    s.AddSingleton(observed);
                    s.AddTopHatRequestTransform<RecordingTransformA>();
                    s.AddTopHatRequestTransform<RecordingTransformB>();
                });

            using (var req = JsonPost("{\"model\":\"x\"}"))
            {
                using (var response = await client.SendAsync(req))
                {
                }
            }

            Assert.Equal(new[] { "A", "B" }, observed);
        }
    }

    [Fact]
    public async Task ExplicitOrderOverridesRegistrationOrder()
    {
        var observed = new List<string>();
        (var client, var _, var _) = TransformHandlerFactory.Build(
            s =>
            {
                s.AddSingleton(observed);
                s.AddTopHatRequestTransform<RecordingTransformA>(cfg => cfg.WithOrder(10));
                s.AddTopHatRequestTransform<RecordingTransformB>(cfg => cfg.WithOrder(1));
            });

        using (var req = JsonPost("{\"model\":\"x\"}"))
        {
            using (var response = await client.SendAsync(req))
            {
            }
        }

        Assert.Equal(new[] { "B", "A" }, observed);
    }

    [Fact]
    public async Task FailOpen_TransformException_DoesNotPropagate_AndPipelineContinues()
    {
        using (var capture = new MetricsCapture())
        {
            var subsequentRan = false;
            (var client, var _, var _) = TransformHandlerFactory.Build(
                s =>
                {
                    s.AddSingleton<Action>(() => subsequentRan = true);
                    s.AddTopHatRequestTransform<ThrowingTransform>();
                    s.AddTopHatRequestTransform<CallbackTransform>();
                });

            using (var req = JsonPost("{\"model\":\"x\"}"))
            {
                using (var response = await client.SendAsync(req))
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                }
            }

            Assert.True(subsequentRan, "subsequent transform should run after fail-open");
            var errors = capture.ForInstrument("tophat.transform.errors").ToList();
            var err = Assert.Single(errors);
            Assert.Equal("ThrowingTransform", err.Tag("transform_name"));
            Assert.Equal("FailOpen", err.Tag("failure_mode"));
        }
    }

    [Fact]
    public async Task FailClosed_TransformException_SurfacesAsHttpRequestException()
    {
        (var client, var _, var _) = TransformHandlerFactory.Build(
            s => s.AddTopHatRequestTransform<ThrowingTransform>(cfg => cfg.WithFailureMode(TransformFailureMode.FailClosed)));

        using (var req = JsonPost("{\"model\":\"x\"}"))
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(req));
        }
    }

    [Fact]
    public void CircuitBreakerFailureMode_ThrowsAtRegistration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTopHat();

        Assert.Throws<NotImplementedException>(() =>
            services.AddTopHatRequestTransform<NoOpRequestTransform>(cfg => cfg.WithFailureMode(TransformFailureMode.CircuitBreaker)));
    }

    [Fact]
    public void ResponseTransformRegistration_SucceedsInM5()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTopHat();

        // M5 wires response transforms end-to-end. Registration must not throw.
        services.AddTopHatResponseTransform<DummyResponseTransform>();

        var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<TopHatTransformRegistry>();
        Assert.Contains(registry.Registrations, r => r.TransformType == typeof(DummyResponseTransform));
    }

    [Fact]
    public async Task BypassedRequest_TransformsNotInvoked()
    {
        using (var capture = new MetricsCapture())
        {
            (var client, var _, var _) = TransformHandlerFactory.Build(
                s => s.AddTopHatRequestTransform<NoOpRequestTransform>());

            using (var req = JsonPost("{\"model\":\"x\"}"))
            {
                req.Options.Set(new HttpRequestOptionsKey<bool>(TopHatHandler.BypassOptionsKey), true);
                using (var response = await client.SendAsync(req))
                {
                }
            }

            Assert.Empty(capture.ForInstrument("tophat.transform.invoked"));
        }
    }

    [Fact]
    public async Task FailOpen_BodyRestoredFromSnapshot_ForNextTransform()
    {
        (var client, var _, var _) = TransformHandlerFactory.Build(
            s =>
            {
                s.AddTopHatRequestTransform<MutateThenThrowTransform>();
                s.AddTopHatRequestTransform<AssertCleanBodyTransform>();
            });

        using (var req = JsonPost("{\"model\":\"x\",\"original\":true}"))
        {
            using (var response = await client.SendAsync(req))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }
    }

    private static HttpRequestMessage JsonPost(string json) =>
        new(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    internal sealed class RecordingTransformA : IRequestTransform
    {
        private readonly List<string> _observed;

        public RecordingTransformA(List<string> observed) => this._observed = observed;

        public ValueTask InvokeAsync(RequestTransformContext context, CancellationToken cancellationToken)
        {
            this._observed.Add("A");
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class RecordingTransformB : IRequestTransform
    {
        private readonly List<string> _observed;

        public RecordingTransformB(List<string> observed) => this._observed = observed;

        public ValueTask InvokeAsync(RequestTransformContext context, CancellationToken cancellationToken)
        {
            this._observed.Add("B");
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class ThrowingTransform : IRequestTransform
    {
        public ValueTask InvokeAsync(RequestTransformContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("intentional test failure");
    }

    internal sealed class CallbackTransform : IRequestTransform
    {
        private readonly Action _callback;

        public CallbackTransform(Action callback) => this._callback = callback;

        public ValueTask InvokeAsync(RequestTransformContext context, CancellationToken cancellationToken)
        {
            this._callback();
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class MutateThenThrowTransform : IRequestTransform
    {
        public ValueTask InvokeAsync(RequestTransformContext context, CancellationToken cancellationToken)
        {
            if (context.Body is JsonObject obj)
            {
                obj["polluted"] = true;
                obj.Remove("original");
            }

            throw new InvalidOperationException("after mutation");
        }
    }

    internal sealed class AssertCleanBodyTransform : IRequestTransform
    {
        public ValueTask InvokeAsync(RequestTransformContext context, CancellationToken cancellationToken)
        {
            Assert.NotNull(context.Body);
            Assert.IsType<JsonObject>(context.Body);
            var obj = (JsonObject)context.Body!;
            Assert.True(obj.ContainsKey("original"), "body should be restored to pre-mutation state");
            Assert.False(obj.ContainsKey("polluted"), "polluted field from prior failed transform must be discarded");
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class DummyResponseTransform : IResponseTransform
    {
        public ValueTask InvokeAsync(ResponseTransformContext context, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
