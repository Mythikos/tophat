using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text.Json.Nodes;
using TopHat.Diagnostics;
using TopHat.Providers;
using TopHat.Transforms.Common;

namespace TopHat.Transforms.PromptStabilizer;

/// <summary>
/// OpenAI prompt stabilizer. Targets <see cref="TopHatTarget.OpenAIChatCompletions"/> and
/// <see cref="TopHatTarget.OpenAIResponses"/>. Unlike M4's Anthropic cache aligner, this transform
/// never injects <c>cache_control</c> markers — OpenAI has no such concept; its prefix cache
/// activates automatically when the first ≥1024 tokens are byte-identical to a recent request.
/// </summary>
/// <remarks>
/// <para>Default behavior (dynamic extraction OFF): the transform is a pass-through that records
/// metrics/logs confirming the request is eligible for caching. Useful as evidence that the
/// pipeline fired.</para>
/// <para>Dynamic extraction ON: detects ISO 8601 dates, UUIDs, and user-supplied patterns in the
/// leading system message (chat) or <c>instructions</c> string (responses), and moves them to a
/// trailing position so the stable prefix stays byte-identical across calls.</para>
/// </remarks>
public sealed class OpenAiPromptStabilizerTransform : IRequestTransform
{
    private readonly OpenAiPromptStabilizerOptions _options;

    public OpenAiPromptStabilizerTransform(IOptions<OpenAiPromptStabilizerOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this._options = options.Value;
    }

    public ValueTask InvokeAsync(RequestTransformContext context, CancellationToken cancellationToken)
    {
        if (context.Body is not JsonObject body)
        {
            return ValueTask.CompletedTask;
        }

        var plan = OpenAiStabilizerPlanner.Plan(body, context.Target, this._options, context.Model);
        if (plan.IsSkip)
        {
            RecordSkip(context, plan.SkipReason.ToTag());
            return ValueTask.CompletedTask;
        }

        var movedSpans = 0;
        if (this._options.ExperimentalDynamicExtraction && this._options.AllowRestructure)
        {
            movedSpans = this.TryApplyDynamicExtraction(body, context, plan);
        }
        else if (this._options.ExperimentalDynamicExtraction && !this._options.AllowRestructure)
        {
            RecordSkip(context, PromptStabilizerSkipReason.RestructureDisallowed.ToTag());
            return ValueTask.CompletedTask;
        }

        var shape = plan.Decision == OpenAiStabilizerDecision.ApplyChat ? "chat" : "responses";
        context.Properties["tophat.prompt_stabilizer.shape"] = shape;
        context.Properties["tophat.prompt_stabilizer.moved_spans"] = movedSpans;

        if (movedSpans > 0)
        {
            context.MarkMutated();
        }

        if (context.Logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
        {
#pragma warning disable CA1873
            TopHatLogEvents.PromptStabilizerApplied(context.Logger, shape, context.Model, movedSpans, context.LocalId);
#pragma warning restore CA1873
        }

        return ValueTask.CompletedTask;
    }

    private int TryApplyDynamicExtraction(JsonObject body, RequestTransformContext context, OpenAiStabilizerPlan plan)
    {
        var text = GetExtractionTarget(body, plan);
        if (text is null)
        {
            return 0;
        }

        var extractor = new DynamicPatternExtractor(this._options.DynamicPatterns.ToList(), this._options.DynamicPatternTimeout);
        var spans = extractor.Extract(text, this._options.DynamicExtractionTailFraction, this._options.DynamicExtractionTailMinChars);

        if (extractor.TimeoutCount > 0)
        {
            var tags = new TagList
            {
                { "target", context.Target.ToString() },
                { "transform_name", nameof(OpenAiPromptStabilizerTransform) },
                { "reason", PromptStabilizerSkipReason.RegexTimeout.ToTag() },
            };
            TopHatMetrics.TransformSkipped.Add(extractor.TimeoutCount, tags);
        }

        if (spans.Count == 0)
        {
            return 0;
        }

        return plan.Decision switch
        {
            OpenAiStabilizerDecision.ApplyChat => OpenAiChatRestructurer.ApplyDynamicSplit(body, spans),
            OpenAiStabilizerDecision.ApplyResponses => OpenAiResponsesRestructurer.ApplyDynamicSplit(body, spans),
            _ => 0,
        };
    }

    private static string? GetExtractionTarget(JsonObject body, OpenAiStabilizerPlan plan)
    {
        if (plan.Decision == OpenAiStabilizerDecision.ApplyChat)
        {
            // First system message's string content.
            if (body["messages"] is JsonArray messages)
            {
                foreach (var m in messages)
                {
                    if (m is JsonObject mo && mo["role"] is JsonValue rv && rv.TryGetValue<string>(out var role) && role == "system"
                        && mo["content"] is JsonValue cv && cv.TryGetValue<string>(out var systemText))
                    {
                        return systemText;
                    }
                }
            }

            return null;
        }

        if (plan.Decision == OpenAiStabilizerDecision.ApplyResponses)
        {
            if (body["instructions"] is JsonValue iv && iv.TryGetValue<string>(out var text))
            {
                return text;
            }
        }

        return null;
    }

    private static void RecordSkip(RequestTransformContext context, string reason)
    {
        var tags = new TagList
        {
            { "target", context.Target.ToString() },
            { "transform_name", nameof(OpenAiPromptStabilizerTransform) },
            { "reason", reason },
        };
        TopHatMetrics.TransformSkipped.Add(1, tags);
        TopHatLogEvents.PromptStabilizerSkipped(context.Logger, reason, context.Model, context.LocalId);
    }
}
