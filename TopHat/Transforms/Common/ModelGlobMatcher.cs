namespace TopHat.Transforms.Common;

/// <summary>
/// Tiny glob matcher shared by model allowlists in M4 (Anthropic cache aligner) and M5 (OpenAI
/// prompt stabilizer). Pattern language: <c>*</c> matches <c>[-._a-zA-Z0-9]*</c>. No other
/// metacharacters. Linear-time; no backtracking pathology.
/// </summary>
internal static class ModelGlobMatcher
{
    public static bool GlobMatch(string input, string pattern)
    {
        var i = 0;
        var p = 0;
        while (p < pattern.Length)
        {
            var pc = pattern[p];
            if (pc == '*')
            {
                // Greedy consume the allowed character set, then verify the remaining pattern.
                p++;
                if (p == pattern.Length)
                {
                    // Trailing *: match to end if all remaining chars are allowed-set.
                    while (i < input.Length && IsGlobChar(input[i]))
                    {
                        i++;
                    }

                    return i == input.Length;
                }

                // Non-greedy: try each possible expansion of * (linear because the set is tight).
                while (true)
                {
                    if (GlobMatch(input[i..], pattern[p..]))
                    {
                        return true;
                    }

                    if (i >= input.Length || !IsGlobChar(input[i]))
                    {
                        return false;
                    }

                    i++;
                }
            }

            if (i >= input.Length)
            {
                return false;
            }

            if (pc != input[i])
            {
                return false;
            }

            i++;
            p++;
        }

        return i == input.Length;
    }

    public static bool IsAllowed(string model, IReadOnlyList<string> allowedPatterns)
    {
        for (var i = 0; i < allowedPatterns.Count; i++)
        {
            if (GlobMatch(model, allowedPatterns[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsGlobChar(char c) => c == '-' || c == '.' || c == '_' || (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
}
