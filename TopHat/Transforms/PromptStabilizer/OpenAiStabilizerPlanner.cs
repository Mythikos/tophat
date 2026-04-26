using System.Text.Json.Nodes;
using TopHat.Providers;

namespace TopHat.Transforms.PromptStabilizer;

/// <summary>
/// Decides whether the stabilizer should apply and to which request shape. Output: apply-chat,
/// apply-responses, or skip (with a reason). Char-based proxy at 4 chars/token for the 1024-token
/// threshold.
/// </summary>
internal static class OpenAiStabilizerPlanner
{
    public static OpenAiStabilizerPlan Plan(JsonObject body, TopHatTarget target, OpenAiPromptStabilizerOptions options, string? model)
    {
        // Model allowlist check first. Conservative: unknown → skip.
        if (!OpenAiModelAllowlist.IsAllowed(model, options.AllowedModelPatterns.ToList()))
        {
            return OpenAiStabilizerPlan.Skip(PromptStabilizerSkipReason.UnsupportedOpenAiModel);
        }

        var minChars = Math.Max(1, options.MinimumTokens) * 4;

        return target switch
        {
            TopHatTarget.OpenAIChatCompletions => PlanChat(body, minChars),
            TopHatTarget.OpenAIResponses => PlanResponses(body, minChars),
            _ => OpenAiStabilizerPlan.Skip(PromptStabilizerSkipReason.UnsupportedOpenAiModel),
        };
    }

    private static OpenAiStabilizerPlan PlanChat(JsonObject body, int minChars)
    {
        if (body["messages"] is not JsonArray messages || messages.Count == 0)
        {
            return OpenAiStabilizerPlan.Skip(PromptStabilizerSkipReason.NoSystemOrInstructions);
        }

        // Measure the stable prefix as the first system message's text content length. If no
        // system message, look for the first user message instead (still a reasonable prefix).
        var prefixChars = MeasureLeadingMessageChars(messages);

        if (prefixChars == 0)
        {
            return OpenAiStabilizerPlan.Skip(PromptStabilizerSkipReason.NoSystemOrInstructions);
        }

        if (prefixChars < minChars)
        {
            return OpenAiStabilizerPlan.Skip(PromptStabilizerSkipReason.BelowThreshold, prefixChars);
        }

        return OpenAiStabilizerPlan.Apply(OpenAiStabilizerDecision.ApplyChat, prefixChars);
    }

    private static OpenAiStabilizerPlan PlanResponses(JsonObject body, int minChars)
    {
        // /v1/responses: `instructions` is the primary stable-prefix source.
        var instructions = body["instructions"];
        var instructionsChars = instructions is JsonValue v && v.TryGetValue<string>(out var s) ? s.Length : 0;

        // `input` may be string or array; if absent we still have instructions.
        var inputChars = MeasureResponsesInputChars(body["input"], out var inputShapeSupported);

        if (!inputShapeSupported)
        {
            return OpenAiStabilizerPlan.Skip(PromptStabilizerSkipReason.ResponsesInputShapeUnsupported);
        }

        var prefixChars = instructionsChars + inputChars;

        if (prefixChars == 0)
        {
            return OpenAiStabilizerPlan.Skip(PromptStabilizerSkipReason.NoSystemOrInstructions);
        }

        if (prefixChars < minChars)
        {
            return OpenAiStabilizerPlan.Skip(PromptStabilizerSkipReason.BelowThreshold, prefixChars);
        }

        return OpenAiStabilizerPlan.Apply(OpenAiStabilizerDecision.ApplyResponses, prefixChars);
    }

    private static int MeasureLeadingMessageChars(JsonArray messages)
    {
        // Walk leading messages until roles flip away from system. Count every leading system
        // message's text. If no system messages, count the first user message's text.
        var total = 0;
        var sawSystem = false;
        foreach (var m in messages)
        {
            if (m is not JsonObject mo)
            {
                break;
            }

            var role = mo["role"] as JsonValue;
            if (role is null || !role.TryGetValue<string>(out var roleStr))
            {
                break;
            }

            if (roleStr == "system" || roleStr == "developer")
            {
                total += MeasureContentChars(mo["content"]);
                sawSystem = true;
                continue;
            }

            // First non-system message: if we've seen any system, stop. Otherwise include the first
            // user message as an approximation of the stable prefix.
            if (!sawSystem && roleStr == "user")
            {
                total += MeasureContentChars(mo["content"]);
            }

            break;
        }

        return total;
    }

    private static int MeasureContentChars(JsonNode? content)
    {
        if (content is JsonValue v && v.TryGetValue<string>(out var s))
        {
            return s.Length;
        }

        if (content is JsonArray parts)
        {
            var total = 0;
            foreach (var part in parts)
            {
                if (part is JsonObject po && po["text"] is JsonValue tv && tv.TryGetValue<string>(out var tstr))
                {
                    total += tstr.Length;
                }
            }

            return total;
        }

        return 0;
    }

    private static int MeasureResponsesInputChars(JsonNode? input, out bool shapeSupported)
    {
        shapeSupported = true;

        if (input is null)
        {
            return 0;
        }

        if (input is JsonValue iv && iv.TryGetValue<string>(out var istr))
        {
            return istr.Length;
        }

        if (input is JsonArray items)
        {
            var total = 0;
            foreach (var item in items)
            {
                if (item is not JsonObject io)
                {
                    shapeSupported = false;
                    return 0;
                }

                if (io["content"] is JsonArray contentParts)
                {
                    foreach (var part in contentParts)
                    {
                        if (part is not JsonObject po)
                        {
                            shapeSupported = false;
                            return 0;
                        }

                        var type = po["type"] as JsonValue;
                        var typeStr = type is not null && type.TryGetValue<string>(out var tst) ? tst : null;
                        if (typeStr == "input_text")
                        {
                            if (po["text"] is JsonValue tv && tv.TryGetValue<string>(out var ttxt))
                            {
                                total += ttxt.Length;
                            }
                        }
                        else if (typeStr != null && typeStr != "output_text")
                        {
                            // Images, files, audio — we can't safely reorder these. Skip.
                            shapeSupported = false;
                            return 0;
                        }
                    }
                }
                else if (io["content"] is JsonValue cv && cv.TryGetValue<string>(out var cstr))
                {
                    total += cstr.Length;
                }
            }

            return total;
        }

        shapeSupported = false;
        return 0;
    }
}
