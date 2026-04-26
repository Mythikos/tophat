using System.Text.Json;
using System.Text.Json.Nodes;

namespace TopHat.Transforms.CacheAligner;

/// <summary>
/// Plans where to place <c>cache_control</c> markers given a request body and model-specific
/// token threshold. Returns either a list of breakpoints to apply or a skip reason.
/// </summary>
/// <remarks>
/// Locked decision #1: one breakpoint by default (end-of-system, falling back to end-of-tools).
/// <see cref="AnthropicCacheAlignerOptions.CacheToolsIndependently"/> opts into a two-breakpoint
/// layout. The common case is one.
/// </remarks>
internal static class CacheAlignerBreakpointPlanner
{
    /// <summary>Characters-per-token proxy for threshold checks.</summary>
    private const int CHARS_PER_TOKEN_PROXY = 4;

    public static CacheAlignerPlan Plan(JsonObject body, int minTokens, bool cacheToolsIndependently, bool allowSystemRestructure)
    {
        // Pre-check: any cache_control marker already present anywhere → skip.
        if (HasAnyCacheControl(body))
        {
            return CacheAlignerPlan.Skip(CacheAlignerSkipReason.AlreadyOptimized);
        }

        var systemNode = body["system"];
        var toolsNode = body["tools"];
        var systemChars = MeasureSystemChars(systemNode);
        var toolsChars = MeasureToolsChars(toolsNode);

        if (systemNode is null && toolsNode is null)
        {
            return CacheAlignerPlan.Skip(CacheAlignerSkipReason.NoSystemOrTools);
        }

        var thresholdChars = (long)minTokens * CHARS_PER_TOKEN_PROXY;
        var breakpoints = new List<BreakpointKind>(capacity: 2);
        var prefixChars = 0;
        if (cacheToolsIndependently)
        {
            // Independent: each segment must pass threshold on its own.
            if (toolsNode is JsonArray toolsArr && toolsArr.Count > 0 && toolsChars >= thresholdChars)
            {
                breakpoints.Add(BreakpointKind.ToolsEnd);
                prefixChars += toolsChars;
            }

            if (CanMarkSystem(systemNode, allowSystemRestructure) && systemChars >= thresholdChars)
            {
                breakpoints.Add(BreakpointKind.SystemEnd);
                prefixChars += systemChars;
            }

            if (breakpoints.Count == 0)
            {
                return ClassifyEmptyPlacement(systemNode, toolsNode, allowSystemRestructure);
            }

            return CacheAlignerPlan.Place(breakpoints, prefixChars);
        }

        // Default (one breakpoint): prefix cache covers tools+system from end-of-system.
        // Combined size threshold.
        var combined = systemChars + toolsChars;
        if (combined < thresholdChars)
        {
            return CacheAlignerPlan.Skip(CacheAlignerSkipReason.BelowThreshold);
        }

        if (CanMarkSystem(systemNode, allowSystemRestructure))
        {
            breakpoints.Add(BreakpointKind.SystemEnd);
            return CacheAlignerPlan.Place(breakpoints, combined);
        }

        // System wasn't markable (missing, or string + restructure disallowed). Fall back to tools.
        if (toolsNode is JsonArray fallbackTools && fallbackTools.Count > 0)
        {
            breakpoints.Add(BreakpointKind.ToolsEnd);
            return CacheAlignerPlan.Place(breakpoints, toolsChars);
        }

        return ClassifyEmptyPlacement(systemNode, toolsNode, allowSystemRestructure);
    }

    private static CacheAlignerPlan ClassifyEmptyPlacement(JsonNode? systemNode, JsonNode? toolsNode, bool allowSystemRestructure)
    {
        // If we refused to mark because of the restructure gate, that's the reason.
        if (systemNode is JsonValue && !allowSystemRestructure)
        {
            return CacheAlignerPlan.Skip(CacheAlignerSkipReason.SystemRestructureDisallowed);
        }

        // Otherwise we couldn't mark because nothing was cacheable.
        return CacheAlignerPlan.Skip(
            systemNode is null && toolsNode is null
                ? CacheAlignerSkipReason.NoSystemOrTools
                : CacheAlignerSkipReason.BelowThreshold);
    }

    private static bool CanMarkSystem(JsonNode? systemNode, bool allowSystemRestructure) => systemNode switch
    {
        null => false,
        JsonArray arr => arr.Count > 0,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => allowSystemRestructure,
        _ => false,
    };

    private static int MeasureSystemChars(JsonNode? systemNode)
    {
        if (systemNode is null)
        {
            return 0;
        }

        if (systemNode is JsonValue v && v.GetValueKind() == JsonValueKind.String)
        {
            return v.GetValue<string>().Length;
        }

        if (systemNode is JsonArray arr)
        {
            var total = 0;
            foreach (var element in arr)
            {
                if (element is JsonObject obj && obj["text"] is JsonValue tv && tv.GetValueKind() == JsonValueKind.String)
                {
                    total += tv.GetValue<string>().Length;
                }
            }

            return total;
        }

        return 0;
    }

    private static int MeasureToolsChars(JsonNode? toolsNode)
    {
        if (toolsNode is not JsonArray arr || arr.Count == 0)
        {
            return 0;
        }

        // Char count of the serialized tools array is a reasonable proxy for its cached-token cost.
        return arr.ToJsonString().Length;
    }

    public static bool HasAnyCacheControl(JsonNode? root)
    {
        if (root is null)
        {
            return false;
        }

        switch (root)
        {
            case JsonObject obj:
                foreach (var kvp in obj)
                {
                    if (kvp.Key == "cache_control")
                    {
                        return true;
                    }

                    if (HasAnyCacheControl(kvp.Value))
                    {
                        return true;
                    }
                }

                return false;

            case JsonArray arr:
                foreach (var element in arr)
                {
                    if (HasAnyCacheControl(element))
                    {
                        return true;
                    }
                }

                return false;

            default:
                return false;
        }
    }
}
