using System.Text.Json.Nodes;
using TopHat.Transforms.CacheAligner;
using Xunit;

namespace TopHat.Tests.Transforms.CacheAligner;

public sealed class CacheAlignerBreakpointPlannerTests
{
    // ~10K chars; comfortably above every current Anthropic minimum for threshold tests.
    private static readonly string s_longText = new('x', 10000);

    private static readonly string s_shortText = new('x', 200);  // well below 1024*4 chars

    [Fact]
    public void StringSystem_NoTools_DefaultConfig_OneBreakpointSystemEnd()
    {
        var body = Obj().WithSystem(s_longText).Build();
        var plan = CacheAlignerBreakpointPlanner.Plan(body, minTokens: 1024, cacheToolsIndependently: false, allowSystemRestructure: true);

        Assert.False(plan.IsSkip);
        Assert.Single(plan.Breakpoints);
        Assert.Equal(BreakpointKind.SystemEnd, plan.Breakpoints[0]);
    }

    [Fact]
    public void ArraySystem_NoTools_DefaultConfig_OneBreakpointSystemEnd()
    {
        var body = Obj().WithSystemArray([s_longText]).Build();
        var plan = CacheAlignerBreakpointPlanner.Plan(body, minTokens: 1024, cacheToolsIndependently: false, allowSystemRestructure: true);

        Assert.False(plan.IsSkip);
        Assert.Single(plan.Breakpoints);
        Assert.Equal(BreakpointKind.SystemEnd, plan.Breakpoints[0]);
    }

    [Fact]
    public void SystemPlusTools_DefaultConfig_OneBreakpointSystemEndOnly()
    {
        // Locked decision #1: tools NOT separately marked by default.
        var body = Obj().WithSystem(s_longText).WithTools(ToolsArray(s_longText)).Build();
        var plan = CacheAlignerBreakpointPlanner.Plan(body, minTokens: 1024, cacheToolsIndependently: false, allowSystemRestructure: true);

        Assert.False(plan.IsSkip);
        Assert.Single(plan.Breakpoints);
        Assert.Equal(BreakpointKind.SystemEnd, plan.Breakpoints[0]);
    }

    [Fact]
    public void SystemPlusTools_CacheToolsIndependently_BothAboveThreshold_TwoBreakpoints()
    {
        var body = Obj().WithSystem(s_longText).WithTools(ToolsArray(s_longText)).Build();
        var plan = CacheAlignerBreakpointPlanner.Plan(body, minTokens: 1024, cacheToolsIndependently: true, allowSystemRestructure: true);

        Assert.False(plan.IsSkip);
        Assert.Equal(2, plan.Breakpoints.Count);
        Assert.Contains(BreakpointKind.ToolsEnd, plan.Breakpoints);
        Assert.Contains(BreakpointKind.SystemEnd, plan.Breakpoints);
    }

    [Fact]
    public void SystemPlusTools_CacheToolsIndependently_OnlyToolsAboveThreshold_OneBreakpointTools()
    {
        var body = Obj().WithSystem(s_shortText).WithTools(ToolsArray(s_longText)).Build();
        var plan = CacheAlignerBreakpointPlanner.Plan(body, minTokens: 1024, cacheToolsIndependently: true, allowSystemRestructure: true);

        Assert.False(plan.IsSkip);
        Assert.Single(plan.Breakpoints);
        Assert.Equal(BreakpointKind.ToolsEnd, plan.Breakpoints[0]);
    }

    [Fact]
    public void SystemPlusTools_CacheToolsIndependently_OnlySystemAboveThreshold_OneBreakpointSystem()
    {
        var body = Obj().WithSystem(s_longText).WithTools(ToolsArray(s_shortText)).Build();
        var plan = CacheAlignerBreakpointPlanner.Plan(body, minTokens: 1024, cacheToolsIndependently: true, allowSystemRestructure: true);

        Assert.False(plan.IsSkip);
        Assert.Single(plan.Breakpoints);
        Assert.Equal(BreakpointKind.SystemEnd, plan.Breakpoints[0]);
    }

    [Fact]
    public void ToolsOnly_NoSystem_DefaultConfig_OneBreakpointToolsEnd()
    {
        var body = Obj().WithTools(ToolsArray(s_longText)).Build();
        var plan = CacheAlignerBreakpointPlanner.Plan(body, minTokens: 1024, cacheToolsIndependently: false, allowSystemRestructure: true);

        Assert.False(plan.IsSkip);
        Assert.Single(plan.Breakpoints);
        Assert.Equal(BreakpointKind.ToolsEnd, plan.Breakpoints[0]);
    }

    [Fact]
    public void EverythingSubThreshold_SkipsWithBelowThreshold()
    {
        var body = Obj().WithSystem(s_shortText).Build();
        var plan = CacheAlignerBreakpointPlanner.Plan(body, minTokens: 1024, cacheToolsIndependently: false, allowSystemRestructure: true);

        Assert.True(plan.IsSkip);
        Assert.Equal(CacheAlignerSkipReason.BelowThreshold, plan.SkipReason);
    }

    [Fact]
    public void NoSystemOrTools_SkipsWithNoSystemOrTools()
    {
        var body = Obj().Build();
        var plan = CacheAlignerBreakpointPlanner.Plan(body, minTokens: 1024, cacheToolsIndependently: false, allowSystemRestructure: true);

        Assert.True(plan.IsSkip);
        Assert.Equal(CacheAlignerSkipReason.NoSystemOrTools, plan.SkipReason);
    }

    [Fact]
    public void StringSystem_RestructureDisallowed_SkipsWithSystemRestructureDisallowed()
    {
        var body = Obj().WithSystem(s_longText).Build();
        var plan = CacheAlignerBreakpointPlanner.Plan(body, minTokens: 1024, cacheToolsIndependently: false, allowSystemRestructure: false);

        Assert.True(plan.IsSkip);
        Assert.Equal(CacheAlignerSkipReason.SystemRestructureDisallowed, plan.SkipReason);
    }

    [Fact]
    public void StringSystem_RestructureDisallowed_WithTools_FallsBackToToolsMarker()
    {
        var body = Obj().WithSystem(s_longText).WithTools(ToolsArray(s_longText)).Build();
        var plan = CacheAlignerBreakpointPlanner.Plan(body, minTokens: 1024, cacheToolsIndependently: false, allowSystemRestructure: false);

        Assert.False(plan.IsSkip);
        Assert.Single(plan.Breakpoints);
        Assert.Equal(BreakpointKind.ToolsEnd, plan.Breakpoints[0]);
    }

    [Theory]
    [InlineData("top_level")]
    [InlineData("inside_tool")]
    [InlineData("inside_system_block")]
    [InlineData("inside_message_block")]
    public void ExistingCacheControl_AnyDepth_ShortCircuitsAsAlreadyOptimized(string location)
    {
        var body = Obj().WithSystemArray([s_longText]).WithTools(ToolsArray(s_longText)).Build();

        // Inject cache_control at the specified location.
        switch (location)
        {
            case "top_level":
                body["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
                break;
            case "inside_tool":
                ((JsonArray)body["tools"]!)[0]!["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
                break;
            case "inside_system_block":
                ((JsonArray)body["system"]!)[0]!["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
                break;
            case "inside_message_block":
                body["messages"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "text",
                                ["text"] = "hi",
                                ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
                            },
                        },
                    },
                };
                break;
        }

        var plan = CacheAlignerBreakpointPlanner.Plan(body, minTokens: 1024, cacheToolsIndependently: false, allowSystemRestructure: true);

        Assert.True(plan.IsSkip);
        Assert.Equal(CacheAlignerSkipReason.AlreadyOptimized, plan.SkipReason);
    }

    private static BodyBuilder Obj() => new();

    private static JsonArray ToolsArray(string descriptionText)
    {
        // A tool definition with enough char weight to pass threshold when needed.
        return new JsonArray
        {
            new JsonObject
            {
                ["name"] = "sample_tool",
                ["description"] = descriptionText,
                ["input_schema"] = new JsonObject { ["type"] = "object" },
            },
        };
    }

    internal sealed class BodyBuilder
    {
        private readonly JsonObject _obj = new() { ["model"] = "claude-haiku-4-5-20251001" };

        public BodyBuilder WithSystem(string text)
        {
            this._obj["system"] = text;
            return this;
        }

        public BodyBuilder WithSystemArray(string[] textBlocks)
        {
            var arr = new JsonArray();
            foreach (var t in textBlocks)
            {
                arr.Add(new JsonObject { ["type"] = "text", ["text"] = t });
            }

            this._obj["system"] = arr;
            return this;
        }

        public BodyBuilder WithTools(JsonArray tools)
        {
            this._obj["tools"] = tools;
            return this;
        }

        public JsonObject Build() => this._obj;
    }
}
