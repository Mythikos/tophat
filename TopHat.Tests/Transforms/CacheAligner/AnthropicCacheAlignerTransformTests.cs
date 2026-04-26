using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TopHat.DependencyInjection;
using TopHat.Tests.Support;
using TopHat.Transforms;
using TopHat.Transforms.CacheAligner;
using Xunit;

namespace TopHat.Tests.Transforms.CacheAligner;

public sealed class AnthropicCacheAlignerTransformTests
{
    private static readonly string s_longSystem = new('x', 10000);  // above any current threshold
    private static readonly string s_shortSystem = new('x', 200);

    [Fact]
    public async Task StringSystemNoTools_AppliesCacheControlToWrappedBlock()
    {
        var received = await Run(Body.WithStringSystem(s_longSystem));

        var system = received["system"] as JsonArray;
        Assert.NotNull(system);
        Assert.Single(system!);
        Assert.NotNull(((JsonObject)system![0]!)["cache_control"]);
    }

    [Fact]
    public async Task ArraySystem_AppliesCacheControlToLastElement()
    {
        var received = await Run(Body.WithArraySystem([s_longSystem]));

        var last = (JsonObject)((JsonArray)received["system"]!)[0]!;
        Assert.NotNull(last["cache_control"]);
    }

    [Fact]
    public async Task SystemPlusTools_DefaultMode_MarksSystemOnly()
    {
        var received = await Run(Body.WithStringSystem(s_longSystem).WithTools(ToolsArray(s_longSystem)));

        // Tools should have NO cache_control.
        var tools = (JsonArray)received["tools"]!;
        Assert.False(((JsonObject)tools[0]!).ContainsKey("cache_control"));

        // System should have it.
        var system = (JsonArray)received["system"]!;
        Assert.NotNull(((JsonObject)system[0]!)["cache_control"]);
    }

    [Fact]
    public async Task SystemPlusTools_CacheToolsIndependently_MarksBoth()
    {
        var received = await Run(
            Body.WithStringSystem(s_longSystem).WithTools(ToolsArray(s_longSystem)),
            o => o.CacheToolsIndependently = true);

        var tools = (JsonArray)received["tools"]!;
        Assert.NotNull(((JsonObject)tools[0]!)["cache_control"]);

        var system = (JsonArray)received["system"]!;
        Assert.NotNull(((JsonObject)system[0]!)["cache_control"]);
    }

    [Fact]
    public async Task ToolsOnlyNoSystem_MarksLastTool()
    {
        var received = await Run(Body.WithToolsOnly(ToolsArray(s_longSystem)));

        var tools = (JsonArray)received["tools"]!;
        Assert.NotNull(((JsonObject)tools[0]!)["cache_control"]);
    }

    [Fact]
    public async Task ExistingMultiBlockSystem_MarksLastBlock()
    {
        var received = await Run(Body.WithArraySystem([s_longSystem, "second block"]));

        var arr = (JsonArray)received["system"]!;
        // First block unchanged.
        Assert.False(((JsonObject)arr[0]!).ContainsKey("cache_control"));
        // Last block marked.
        Assert.NotNull(((JsonObject)arr[1]!)["cache_control"]);
    }

    [Fact]
    public async Task MessagesWithTextAndImage_PathStillSucceeds_DoesNotMarkImage()
    {
        var body = Body.WithStringSystem(s_longSystem);
        body.Raw["messages"] = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = "what's in this image?" },
                    new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["data"] = "x" } },
                },
            },
        };

        var received = await Run(body);

        // System got marked; messages unchanged.
        Assert.NotNull(((JsonObject)((JsonArray)received["system"]!)[0]!)["cache_control"]);
        var msgContent = (JsonArray)((JsonObject)((JsonArray)received["messages"]!)[0]!)["content"]!;
        Assert.False(((JsonObject)msgContent[0]!).ContainsKey("cache_control"));
        Assert.False(((JsonObject)msgContent[1]!).ContainsKey("cache_control"));
    }

    [Fact]
    public async Task StringSystem_AllowSystemRestructureFalse_ModeA_SkipsWithRestructureReason()
    {
        using (var capture = new MetricsCapture())
        {
            // Expect no mutation (original string system preserved).
            var received = await Run(
                Body.WithStringSystem(s_longSystem),
                o => o.AllowSystemRestructure = false);

            Assert.True(received["system"] is JsonValue);  // still string

            var skipped = capture.ForInstrument("tophat.transform.skipped").ToList();
            Assert.Contains(skipped, r => (string?)r.Tag("reason") == "system_restructure_disallowed");
        }
    }

    [Fact]
    public async Task ArraySystem_AllowSystemRestructureFalse_StillMarks()
    {
        var received = await Run(
            Body.WithArraySystem([s_longSystem]),
            o => o.AllowSystemRestructure = false);

        Assert.NotNull(((JsonObject)((JsonArray)received["system"]!)[0]!)["cache_control"]);
    }

    [Fact]
    public async Task ToolsOnly_AllowSystemRestructureFalse_StillMarks()
    {
        var received = await Run(
            Body.WithToolsOnly(ToolsArray(s_longSystem)),
            o => o.AllowSystemRestructure = false);

        Assert.NotNull(((JsonObject)((JsonArray)received["tools"]!)[0]!)["cache_control"]);
    }

    [Fact]
    public async Task ModelNotInAllowlist_SkipsWithUnsupportedModel()
    {
        using (var capture = new MetricsCapture())
        {
            var body = Body.WithStringSystem(s_longSystem);
            body.Raw["model"] = "gpt-4o";  // explicitly not Claude
            var received = await Run(body);

            Assert.True(received["system"] is JsonValue);  // unchanged
            Assert.Contains(
                capture.ForInstrument("tophat.transform.skipped").ToList(),
                r => (string?)r.Tag("reason") == "unsupported_model");
        }
    }

    [Fact]
    public async Task BelowThreshold_Skips()
    {
        using (var capture = new MetricsCapture())
        {
            var received = await Run(Body.WithStringSystem(s_shortSystem));

            Assert.True(received["system"] is JsonValue);
            Assert.Contains(
                capture.ForInstrument("tophat.transform.skipped").ToList(),
                r => (string?)r.Tag("reason") == "below_threshold");
        }
    }

    [Fact]
    public async Task ExistingCacheControl_SkipsWithAlreadyOptimized()
    {
        using (var capture = new MetricsCapture())
        {
            var body = Body.WithArraySystem([s_longSystem]);
            ((JsonObject)((JsonArray)body.Raw["system"]!)[0]!)["cache_control"] = new JsonObject { ["type"] = "ephemeral" };

            var received = await Run(body);

            // Still marked (but by the consumer, not us).
            Assert.NotNull(((JsonObject)((JsonArray)received["system"]!)[0]!)["cache_control"]);
            Assert.Contains(
                capture.ForInstrument("tophat.transform.skipped").ToList(),
                r => (string?)r.Tag("reason") == "already_optimized");
        }
    }

    [Fact]
    public async Task ModeB_WithDateInFirstHalf_SplitsStableAndDynamic()
    {
        var system = "You are a helpful assistant. Today is 2026-04-20. " + new string('y', 5000);
        var received = await Run(
            Body.WithStringSystem(system),
            o => o.ExperimentalDynamicExtraction = true);

        var arr = received["system"] as JsonArray;
        Assert.NotNull(arr);
        Assert.Equal(2, arr!.Count);
        var stable = ((JsonObject)arr[0]!)["text"]!.GetValue<string>();
        var dynamic = ((JsonObject)arr[1]!)["text"]!.GetValue<string>();
        Assert.DoesNotContain("2026-04-20", stable, StringComparison.Ordinal);
        Assert.Contains("2026-04-20", dynamic, StringComparison.Ordinal);
        Assert.NotNull(((JsonObject)arr[0]!)["cache_control"]);
    }

    [Fact]
    public async Task ModeB_CatastrophicUserRegex_TimesOut_RecordsSkipReason_TransformStillApplies()
    {
        using (var capture = new MetricsCapture())
        {
            // Classic ReDoS: (a+)+b against input full of 'a' never terminates — backtracks
            // through exponential combinations before giving up.
            var bad = new Regex("(a+)+b", RegexOptions.None, TimeSpan.FromMilliseconds(10));
            var system = "You are a helpful assistant. " + new string('a', 30000);
            var received = await Run(
                Body.WithStringSystem(system),
                o =>
                {
                    o.ExperimentalDynamicExtraction = true;
                    o.DynamicPatterns.Add(bad);
                });

            // The transform should still apply (Mode A path at least).
            Assert.True(received["system"] is JsonArray);

            // The regex timeout should have been surfaced.
            var skipped = capture.ForInstrument("tophat.transform.skipped").ToList();
            Assert.Contains(skipped, r => (string?)r.Tag("reason") == "regex_timeout");
        }
    }

    [Fact]
    public async Task PropertiesContainsBreakpointKinds_AsStrings()
    {
        (var capturedBytes, var properties) = await RunCapturingProperties(Body.WithStringSystem(s_longSystem));

        Assert.NotNull(capturedBytes);
        Assert.True(properties.ContainsKey("tophat.cache_aligner.breakpoints"));
        var list = (string[])properties["tophat.cache_aligner.breakpoints"]!;
        Assert.Contains("SystemEnd", list);
    }

    [Fact]
    public async Task ParseAndReserializeDoesNotReorderKeys_EvenWhenMutated()
    {
        // Build body with a distinctive top-level key order.
        var original = new JsonObject
        {
            ["system"] = s_longSystem,
            ["model"] = "claude-haiku-4-5-20251001",
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = "hi",
                },
            },
            ["max_tokens"] = 40,
        };

        var received = await Run(Body.FromRaw(original));

        var topLevelKeys = received.Select(kvp => kvp.Key).ToList();
        // 'system' must come before 'model' before 'messages' before 'max_tokens'.
        Assert.Equal(new[] { "system", "model", "messages", "max_tokens" }, topLevelKeys);
    }

    // ------- helpers -------

    private static async Task<JsonObject> Run(Body body, Action<AnthropicCacheAlignerOptions>? configure = null)
    {
        (var received, var _) = await RunCapturingProperties(body, configure);
        return received!;
    }

    private static async Task<(JsonObject? Received, IDictionary<string, object?> Properties)> RunCapturingProperties(
        Body body,
        Action<AnthropicCacheAlignerOptions>? configure = null)
    {
        JsonObject? captured = null;
        IDictionary<string, object?>? capturedProps = null;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTopHat();
        services.AddTopHatAnthropicCacheAligner(configure);
        // A shared sink singleton avoids the transient-vs-singleton-registration mismatch when
        // the test resolves PropertyCapturer separately from DI's transient instance.
        var sink = new PropertySink();
        services.AddSingleton(sink);
        services.AddTopHatRequestTransform<PropertyCapturer>(cfg => cfg.WithOrder(101));

        var inner = new MockInnerHandler(async (req, _) =>
        {
            var bytes = await req.Content!.ReadAsByteArrayAsync();
            captured = JsonNode.Parse(bytes) as JsonObject;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        services.AddSingleton(inner);
        services.AddHttpClient("test")
            .AddHttpMessageHandler<TopHat.Handlers.TopHatHandler>()
            .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<MockInnerHandler>());

        using (var provider = services.BuildServiceProvider())
        {
            var factory = provider.GetRequiredService<IHttpClientFactory>();
            var client = factory.CreateClient("test");

            var json = body.Raw.ToJsonString();
            using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            })
            {
                using (var response = await client.SendAsync(req))
                {
                    capturedProps = sink.LastProperties;
                }
            }
        }

        return (captured, capturedProps ?? new Dictionary<string, object?>());
    }

    internal sealed class PropertySink
    {
        public IDictionary<string, object?>? LastProperties { get; set; }
    }

    internal sealed class PropertyCapturer : IRequestTransform
    {
        private readonly PropertySink _sink;

        public PropertyCapturer(PropertySink sink) => this._sink = sink;

        public ValueTask InvokeAsync(RequestTransformContext context, CancellationToken cancellationToken)
        {
            this._sink.LastProperties = new Dictionary<string, object?>(context.Properties);
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class Body
    {
        // Sonnet 4.5 threshold is 1024 tokens ≈ 4096 chars — a 10K-char fixture clears it easily.
        public JsonObject Raw { get; } = new() { ["model"] = "claude-sonnet-4-5-20250929" };

        public static Body WithStringSystem(string text)
        {
            var b = new Body();
            b.Raw["system"] = text;
            return b;
        }

        public static Body WithArraySystem(string[] blocks)
        {
            var b = new Body();
            var arr = new JsonArray();
            foreach (var t in blocks)
            {
                arr.Add(new JsonObject { ["type"] = "text", ["text"] = t });
            }

            b.Raw["system"] = arr;
            return b;
        }

        public Body WithTools(JsonArray tools)
        {
            this.Raw["tools"] = tools;
            return this;
        }

        public static Body WithToolsOnly(JsonArray tools)
        {
            var b = new Body();
            b.Raw["tools"] = tools;
            return b;
        }

        public static Body FromRaw(JsonObject raw)
        {
            var b = new Body();
            b.Raw.Clear();
            foreach (var kvp in raw)
            {
                b.Raw[kvp.Key] = kvp.Value?.DeepClone();
            }

            return b;
        }
    }

    private static JsonArray ToolsArray(string description) =>
        new()
        {
            new JsonObject
            {
                ["name"] = "sample_tool",
                ["description"] = description,
                ["input_schema"] = new JsonObject { ["type"] = "object" },
            },
        };
}
