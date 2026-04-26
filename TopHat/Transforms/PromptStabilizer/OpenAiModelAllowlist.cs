using TopHat.Transforms.Common;

namespace TopHat.Transforms.PromptStabilizer;

/// <summary>
/// Verifies a model string is covered by the allowlist. OpenAI uses a uniform 1024-token cache
/// threshold across supported models, so no per-model table is needed (unlike M4's Anthropic
/// thresholds).
/// </summary>
internal static class OpenAiModelAllowlist
{
    public static bool IsAllowed(string? model, IReadOnlyList<string> allowedPatterns)
    {
        if (string.IsNullOrEmpty(model))
        {
            return false;
        }

        return ModelGlobMatcher.IsAllowed(model, allowedPatterns);
    }
}
