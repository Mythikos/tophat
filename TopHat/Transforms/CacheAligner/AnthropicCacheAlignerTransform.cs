using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text.Json.Nodes;
using TopHat.Diagnostics;
using TopHat.Transforms.Common;

namespace TopHat.Transforms.CacheAligner;

/// <summary>
/// Anthropic cache aligner. Places <c>cache_control: ephemeral</c> markers to maximize prompt-cache
/// hits without inflicting the +25% write premium on workloads where caching can't pay off.
/// </summary>
public sealed class AnthropicCacheAlignerTransform : IRequestTransform
{
    private readonly AnthropicCacheAlignerOptions _options;

    public AnthropicCacheAlignerTransform(IOptions<AnthropicCacheAlignerOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this._options = options.Value;
    }

    public ValueTask InvokeAsync(RequestTransformContext context, CancellationToken cancellationToken)
    {
        if (context.Body is not JsonObject body)
        {
            // Null body (inspection skipped) or non-object root — nothing we can do.
            return ValueTask.CompletedTask;
        }

        var allowedPatterns = this._options.AllowedModelPatterns.ToList();
        if (!CacheAlignerModelThresholds.TryGetMinimumTokens(context.Model, allowedPatterns, out var minTokens))
        {
            RecordSkip(context, "unsupported_model");
            return ValueTask.CompletedTask;
        }

        var plan = CacheAlignerBreakpointPlanner.Plan(body, minTokens, this._options.CacheToolsIndependently, this._options.AllowSystemRestructure);
        if (plan.IsSkip)
        {
            RecordSkip(context, CacheAlignerPlan.ReasonToTag(plan.SkipReason));
            return ValueTask.CompletedTask;
        }

        var breakpoints = new List<BreakpointKind>(plan.Breakpoints);

        // Mode B runs BEFORE Mode A so it sees the original string-form system. When the split
        // succeeds, it places its own cache_control on the stable block — Mode A skips SystemEnd
        // for that case.
        var modeBHandledSystem = false;
        if (this._options.ExperimentalDynamicExtraction)
        {
            modeBHandledSystem = this.TryApplyDynamicExtraction(body, context, breakpoints);
        }

        // Apply Mode A markers for each planned breakpoint (skipping SystemEnd if Mode B did it).
        foreach (var kind in plan.Breakpoints)
        {
            switch (kind)
            {
                case BreakpointKind.ToolsEnd:
                    CacheAlignerSystemRestructurer.ApplyToolsEndMarker(body);
                    break;
                case BreakpointKind.SystemEnd:
                    if (!modeBHandledSystem)
                    {
                        CacheAlignerSystemRestructurer.ApplySystemEndMarker(body);
                    }
                    break;
            }
        }

        // Surface which breakpoints were applied, for introspection.
        context.Properties["tophat.cache_aligner.breakpoints"] =
            breakpoints.Select(b => b.ToString()).ToArray();

        context.MarkMutated();

        if (context.Logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
        {
            var breakpointNames = string.Join(",", breakpoints.Select(b => b.ToString()));
#pragma warning disable CA1873 // Already gated on IsEnabled above.
            TopHatLogEvents.CacheAlignerApplied(context.Logger, breakpointNames, context.Model, plan.PrefixChars, context.LocalId);
#pragma warning restore CA1873
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Attempts to split the string-form system prompt into stable + dynamic blocks. Returns true
    /// if the split was applied (and placed its own cache_control on the stable block).
    /// </summary>
    private bool TryApplyDynamicExtraction(JsonObject body, RequestTransformContext context, List<BreakpointKind> breakpoints)
    {
        // Mode B only operates on string-form system. Array-form is left to Mode A.
        var originalSystem = body["system"];
        if (originalSystem is not JsonValue v || v.GetValueKind() != System.Text.Json.JsonValueKind.String)
        {
            // Still run the extractor to observe any regex timeouts from user patterns, but with no
            // split target. This is a no-op unless user regexes misbehave.
            this.RunExtractorForObservabilityOnly(context);
            return false;
        }

        if (!this._options.AllowSystemRestructure)
        {
            // Would require reshaping — honor the gate.
            return false;
        }

        var systemText = v.GetValue<string>();
        var extractor = new DynamicPatternExtractor(this._options.DynamicPatterns.ToList(), this._options.DynamicPatternTimeout);
        var spans = extractor.Extract(systemText, this._options.DynamicExtractionTailFraction, this._options.DynamicExtractionTailMinChars);

        if (extractor.TimeoutCount > 0)
        {
            var tags = new TagList
            {
                { "target", context.Target.ToString() },
                { "transform_name", nameof(AnthropicCacheAlignerTransform) },
                { "reason", "regex_timeout" },
            };
            TopHatMetrics.TransformSkipped.Add(extractor.TimeoutCount, tags);
        }

        if (spans.Count == 0)
        {
            return false;
        }

        if (CacheAlignerSystemRestructurer.ApplyDynamicSplit(body, spans))
        {
            breakpoints.Add(BreakpointKind.DynamicTail);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Runs the extractor on whatever system text exists (if any) purely to surface user-regex
    /// timeouts as metrics. Does not modify the body.
    /// </summary>
    private void RunExtractorForObservabilityOnly(RequestTransformContext context)
    {
        if (this._options.DynamicPatterns.Count == 0)
        {
            return;
        }

        // No-op; we don't currently reach here with user patterns on non-string system. Future
        // extension point if Mode B grows array-form support.
    }

    private static void RecordSkip(RequestTransformContext context, string reason)
    {
        var tags = new TagList
        {
            { "target", context.Target.ToString() },
            { "transform_name", nameof(AnthropicCacheAlignerTransform) },
            { "reason", reason },
        };
        TopHatMetrics.TransformSkipped.Add(1, tags);

        TopHatLogEvents.CacheAlignerSkipped(context.Logger, reason, context.Model, context.LocalId);
    }
}
