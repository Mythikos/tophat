using System.Text.Json.Nodes;

namespace TopHat.Transforms.CacheAligner;

/// <summary>
/// Rewrites the <c>system</c> field of a request body in-place to add <c>cache_control</c> markers
/// and, when Mode B is active, to split detected dynamic content out to the tail.
/// </summary>
internal static class CacheAlignerSystemRestructurer
{
    /// <summary>
    /// Mode A: wrap a string-form system prompt into a single-element array with cache_control on
    /// that element. Array-form system prompts have cache_control added to the last element.
    /// Either path mutates <paramref name="body"/> in place.
    /// </summary>
    public static void ApplySystemEndMarker(JsonObject body)
    {
        var systemNode = body["system"];
        if (systemNode is null)
        {
            return;
        }

        if (systemNode is JsonArray existingArray && existingArray.Count > 0)
        {
            var last = existingArray[^1];
            if (last is JsonObject lastObj)
            {
                lastObj["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
            }

            return;
        }

        if (systemNode is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String)
        {
            var asArray = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = v.GetValue<string>(),
                    ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
                },
            };
            body["system"] = asArray;
        }
    }

    /// <summary>
    /// Marks the last tool element with cache_control.
    /// </summary>
    public static void ApplyToolsEndMarker(JsonObject body)
    {
        if (body["tools"] is not JsonArray toolsArr || toolsArr.Count == 0)
        {
            return;
        }

        var last = toolsArr[^1];
        if (last is JsonObject lastObj)
        {
            lastObj["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
        }
    }

    /// <summary>
    /// Mode B: given a string-form system prompt and an ordered list of dynamic spans (already
    /// filtered by the tail heuristic), produce a two-block array: <c>[stable, dynamic]</c>, with
    /// cache_control on the stable block. Returns true if any mutation occurred.
    /// </summary>
    public static bool ApplyDynamicSplit(JsonObject body, IReadOnlyList<(int Start, int Length)> spans)
    {
        if (spans.Count == 0)
        {
            return false;
        }

        var systemNode = body["system"];
        if (systemNode is not JsonValue v || v.GetValueKind() != System.Text.Json.JsonValueKind.String)
        {
            return false;
        }

        var text = v.GetValue<string>();

        // Build the "stable" text by removing dynamic spans and the "dynamic" text by concatenating them.
        // Preserve a space between neighboring moved dynamic fragments for readability.
        var stableBuilder = new System.Text.StringBuilder(text.Length);
        var dynamicBuilder = new System.Text.StringBuilder();
        var cursor = 0;
        foreach ((var start, var length) in spans)
        {
            if (start < cursor)
            {
                continue;  // overlapping span; skip defensively
            }

            stableBuilder.Append(text, cursor, start - cursor);
            if (dynamicBuilder.Length > 0)
            {
                dynamicBuilder.Append(' ');
            }

            dynamicBuilder.Append(text, start, length);
            cursor = start + length;
        }

        stableBuilder.Append(text, cursor, text.Length - cursor);

        var stable = stableBuilder.ToString().TrimEnd();
        var dynamicText = dynamicBuilder.ToString();

        if (string.IsNullOrEmpty(stable) || string.IsNullOrEmpty(dynamicText))
        {
            // All text was dynamic, or none was — nothing meaningful to split.
            return false;
        }

        var asArray = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = stable,
                ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
            },
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = dynamicText,
            },
        };
        body["system"] = asArray;
        return true;
    }
}
