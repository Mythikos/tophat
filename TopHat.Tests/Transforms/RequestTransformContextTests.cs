using Microsoft.Extensions.Logging.Abstractions;
using TopHat.Providers;
using TopHat.Transforms;
using Xunit;

namespace TopHat.Tests.Transforms;

public sealed class RequestTransformContextTests
{
    [Fact]
    public void MarkMutated_InvokesCallback()
    {
        var fired = 0;
        var ctx = Build(() => fired++);

        ctx.MarkMutated();

        Assert.Equal(1, fired);
    }

    [Fact]
    public void Properties_RoundTripsValues()
    {
        var ctx = Build(() => { });

        ctx.Properties["foo"] = 42;

        Assert.Equal(42, ctx.Properties["foo"]);
    }

    [Fact]
    public void NullBody_HandledGracefully()
    {
        var ctx = Build(() => { }, body: null);

        Assert.Null(ctx.Body);
    }

    private static RequestTransformContext Build(Action markMutated, System.Text.Json.Nodes.JsonNode? body = null) =>
        new(
            TopHatProviderKind.Anthropic,
            TopHatTarget.AnthropicMessages,
            "model-x",
            streamingFromBody: false,
            localId: "local",
            body: body,
            logger: NullLogger.Instance,
            properties: new Dictionary<string, object?>(),
            markMutatedCallback: markMutated);
}
