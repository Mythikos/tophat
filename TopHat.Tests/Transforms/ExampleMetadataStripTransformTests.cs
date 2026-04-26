using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json.Nodes;
using TopHat.Providers;
using TopHat.Transforms;
using Xunit;

namespace TopHat.Tests.Transforms;

public sealed class ExampleMetadataStripTransformTests
{
    [Fact]
    public async Task FieldPresent_RemovedAndMutatedSignaled()
    {
        var transform = new ExampleMetadataStripTransform(Options.Create(new ExampleMetadataStripOptions()));
        (var ctx, var mutated) = BuildContext(JsonNode.Parse("{\"model\":\"x\",\"metadata\":{\"a\":1}}"));

        await transform.InvokeAsync(ctx, CancellationToken.None);

        var obj = (JsonObject)ctx.Body!;
        Assert.False(obj.ContainsKey("metadata"));
        Assert.True(mutated.Value);
    }

    [Fact]
    public async Task FieldAbsent_NoMutation()
    {
        var transform = new ExampleMetadataStripTransform(Options.Create(new ExampleMetadataStripOptions()));
        (var ctx, var mutated) = BuildContext(JsonNode.Parse("{\"model\":\"x\"}"));

        await transform.InvokeAsync(ctx, CancellationToken.None);

        Assert.False(mutated.Value);
    }

    [Fact]
    public async Task NestedFieldOfSameName_LeftAlone()
    {
        var transform = new ExampleMetadataStripTransform(Options.Create(new ExampleMetadataStripOptions()));
        (var ctx, var mutated) = BuildContext(JsonNode.Parse("{\"wrap\":{\"metadata\":{\"a\":1}}}"));

        await transform.InvokeAsync(ctx, CancellationToken.None);

        // Top-level metadata is absent; nested metadata should be untouched and no mutation reported.
        Assert.False(mutated.Value);
        var obj = (JsonObject)ctx.Body!;
        Assert.True(((JsonObject)obj["wrap"]!).ContainsKey("metadata"));
    }

    [Fact]
    public async Task CustomFieldName_StripsConfiguredField()
    {
        var transform = new ExampleMetadataStripTransform(
            Options.Create(new ExampleMetadataStripOptions { FieldName = "trace_id" }));
        (var ctx, var mutated) = BuildContext(JsonNode.Parse("{\"model\":\"x\",\"trace_id\":\"abc\"}"));

        await transform.InvokeAsync(ctx, CancellationToken.None);

        Assert.True(mutated.Value);
        Assert.False(((JsonObject)ctx.Body!).ContainsKey("trace_id"));
    }

    [Fact]
    public async Task NullBody_NoOp()
    {
        var transform = new ExampleMetadataStripTransform(Options.Create(new ExampleMetadataStripOptions()));
        (var ctx, var mutated) = BuildContext(null);

        await transform.InvokeAsync(ctx, CancellationToken.None);

        Assert.False(mutated.Value);
    }

    private static (RequestTransformContext Context, MutationFlag Flag) BuildContext(JsonNode? body)
    {
        var flag = new MutationFlag();
        var context = new RequestTransformContext(
            TopHatProviderKind.Anthropic,
            TopHatTarget.AnthropicMessages,
            "x",
            false,
            "local",
            body,
            NullLogger.Instance,
            new Dictionary<string, object?>(),
            () => flag.Value = true);
        return (context, flag);
    }

    internal sealed class MutationFlag
    {
        public bool Value { get; set; }
    }
}
