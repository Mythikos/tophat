using System.Text;
using System.Text.Json.Nodes;

namespace TopHat.Transforms.PromptStabilizer;

/// <summary>
/// /v1/responses restructurer. Dynamic-extraction mode only: pulls volatile spans out of the
/// <c>instructions</c> string and appends them as a dynamic-context suffix on the same string.
/// Keeps the stable prefix byte-identical across calls. <c>input</c> is untouched.
/// </summary>
internal static class OpenAiResponsesRestructurer
{
    private const string DynamicHeader = "\n\n---\nDynamic context (moved for prefix stability):\n";

    public static int ApplyDynamicSplit(JsonObject body, IReadOnlyList<(int Start, int Length)> spans)
    {
        if (spans.Count == 0)
        {
            return 0;
        }

        if (body["instructions"] is not JsonValue iv || !iv.TryGetValue<string>(out var instructionsText))
        {
            return 0;
        }

        var (stable, dynamicText) = Split(instructionsText, spans);
        body["instructions"] = stable + DynamicHeader + dynamicText;

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
