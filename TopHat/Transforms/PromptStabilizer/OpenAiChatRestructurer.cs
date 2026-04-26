using System.Text;
using System.Text.Json.Nodes;

namespace TopHat.Transforms.PromptStabilizer;

/// <summary>
/// /v1/chat/completions restructurer. Dynamic-extraction mode only: pulls volatile spans out of
/// the leading system message and relocates them to a newly-appended user message at the tail.
/// The stable prefix stays byte-identical across calls.
/// </summary>
internal static class OpenAiChatRestructurer
{
    private const string DynamicHeader = "\n\n---\nDynamic context (moved for prefix stability):\n";

    /// <summary>
    /// Applies dynamic-extraction splitting to the first system-role message in <paramref name="body"/>.
    /// Returns the number of spans moved, or 0 if nothing was mutated.
    /// </summary>
    public static int ApplyDynamicSplit(JsonObject body, IReadOnlyList<(int Start, int Length)> spans)
    {
        if (spans.Count == 0)
        {
            return 0;
        }

        if (body["messages"] is not JsonArray messages || messages.Count == 0)
        {
            return 0;
        }

        // Find first system message.
        JsonObject? systemMsg = null;
        foreach (var m in messages)
        {
            if (m is JsonObject mo && mo["role"] is JsonValue rv && rv.TryGetValue<string>(out var r) && r == "system")
            {
                systemMsg = mo;
                break;
            }
        }

        if (systemMsg is null)
        {
            return 0;
        }

        if (systemMsg["content"] is not JsonValue cv || !cv.TryGetValue<string>(out var systemText))
        {
            // Array-form content: not handled in M5. Leave as-is.
            return 0;
        }

        var (stable, dynamicText) = Split(systemText, spans);

        // Replace system content with the stable portion.
        systemMsg["content"] = stable;

        // Append a trailing user message with the dynamic content (kept out of the stable prefix).
        var dynamicMsg = new JsonObject
        {
            ["role"] = "user",
            ["content"] = DynamicHeader + dynamicText,
        };
        messages.Add(dynamicMsg);

        return spans.Count;
    }

    private static (string Stable, string Dynamic) Split(string text, IReadOnlyList<(int Start, int Length)> spans)
    {
        var stable = new StringBuilder(text.Length);
        var dyn = new StringBuilder();
        var cursor = 0;

        foreach (var (start, length) in spans)
        {
            if (start < cursor)
            {
                // Overlap safety: skip. Extractor's merge step should prevent this.
                continue;
            }

            if (start > cursor)
            {
                stable.Append(text, cursor, start - cursor);
            }

            if (dyn.Length > 0)
            {
                dyn.Append(" | ");
            }

            dyn.Append(text, start, length);
            cursor = start + length;
        }

        if (cursor < text.Length)
        {
            stable.Append(text, cursor, text.Length - cursor);
        }

        return (stable.ToString(), dyn.ToString());
    }
}
