using TopHat.Transforms.Common;

namespace TopHat.Transforms.CacheAligner;

/// <summary>
/// Maps a Claude model string to its minimum cacheable token count, and validates that the model
/// is in the consumer's allowlist. Pattern language is glob-style: <c>*</c> matches
/// <c>[-._a-zA-Z0-9]*</c>. Same language across the allowlist and the built-in threshold table.
/// </summary>
/// <remarks>
/// First-match-wins ordering is load-bearing — more-specific patterns MUST appear before
/// less-specific ones. <c>CacheAlignerModelThresholdsTests.ThresholdLookup_PrefixCollisions_ResolveToMostSpecific</c>
/// enforces this invariant.
/// </remarks>
internal static class CacheAlignerModelThresholds
{
    // Default fallback when the model passes the allowlist but isn't covered by a more specific
    // pattern below. Conservative: the highest current minimum.
    private const int DEFAULT_UNKNOWN_MINIMUM = 4096;

    private static readonly (string Pattern, int MinTokens)[] s_table =
    [
        ("claude-opus-4-5-*",   4096),
        ("claude-opus-4-6-*",   4096),
        ("claude-opus-4-7-*",   4096),
        ("claude-haiku-4-5-*",  4096),
        ("claude-mythos-*",     4096),
        ("claude-sonnet-4-6-*", 2048),
        ("claude-sonnet-4-5-*", 1024),
        ("claude-sonnet-4-*",   1024),
        ("claude-sonnet-3-7-*", 1024),
        ("claude-opus-4-1-*",   1024),
        ("claude-opus-4-*",     1024),
        ("claude-haiku-3-5-*",  2048),
        ("claude-3-*",          1024),
    ];

    /// <summary>
    /// Returns true and the model's minimum cacheable token count when the model matches at least
    /// one entry in <paramref name="allowedPatterns"/>. Otherwise false (caller should skip).
    /// </summary>
    public static bool TryGetMinimumTokens(string model, IReadOnlyList<string> allowedPatterns, out int minTokens)
    {
        minTokens = 0;
        if (string.IsNullOrEmpty(model))
        {
            return false;
        }

        if (!ModelGlobMatcher.IsAllowed(model, allowedPatterns))
        {
            return false;
        }

        foreach ((var pattern, var value) in s_table)
        {
            if (ModelGlobMatcher.GlobMatch(model, pattern))
            {
                minTokens = value;
                return true;
            }
        }

        minTokens = DEFAULT_UNKNOWN_MINIMUM;
        return true;
    }
}
