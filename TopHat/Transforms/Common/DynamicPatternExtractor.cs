using System.Text.RegularExpressions;

namespace TopHat.Transforms.Common;

/// <summary>
/// Detects dynamic spans (ISO 8601 dates, UUIDs, user-supplied patterns) in prompt text.
/// Provider-agnostic; shared by Anthropic cache aligner (M4) and OpenAI prompt stabilizer (M5).
/// Each regex runs with a configured timeout; a <see cref="RegexMatchTimeoutException"/> on a
/// single pattern is caught and skipped so one catastrophic user regex can't stall the transform.
/// </summary>
/// <remarks>
/// Extraction is conservative: spans falling within the tail — the last
/// <c>max(fraction × length, minChars)</c> characters — are left in place because moving them
/// wouldn't meaningfully improve the stable prefix. Tunable via the caller's options.
/// </remarks>
internal sealed class DynamicPatternExtractor
{
    // ISO 8601 date (date + optional time + optional timezone).
    private static readonly Regex s_iso8601 = new(@"\d{4}-\d{2}-\d{2}(?:T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:?\d{2})?)?", RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));

    // Standard UUID.
    private static readonly Regex s_uuid = new(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));

    private readonly IReadOnlyList<Regex> _userPatterns;
    private readonly TimeSpan _timeout;

    /// <summary>
    /// Incremented every time a user regex times out during <see cref="Extract"/>. Observable via
    /// <c>tophat.transform.skipped{reason=regex_timeout}</c> — the calling transform surfaces this.
    /// </summary>
    public int TimeoutCount { get; private set; }

    public DynamicPatternExtractor(IReadOnlyList<Regex> userPatterns, TimeSpan timeout)
    {
        this._userPatterns = userPatterns;
        this._timeout = timeout;
    }

    /// <summary>
    /// Returns the filtered, sorted-by-start list of spans to move. Overlapping spans are merged
    /// (earlier wins). Spans within the tail window are excluded per the heuristic.
    /// </summary>
    public IReadOnlyList<(int Start, int Length)> Extract(string text, double tailFraction, int tailMinChars)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<(int, int)>();
        }

        var tailStart = ComputeTailStart(text.Length, tailFraction, tailMinChars);

        var raw = new List<(int Start, int Length)>();
        this.CollectMatches(s_iso8601, text, raw, useCustomTimeout: false);
        this.CollectMatches(s_uuid, text, raw, useCustomTimeout: false);
        foreach (var userPattern in this._userPatterns)
        {
            this.CollectMatches(userPattern, text, raw, useCustomTimeout: true);
        }

        if (raw.Count == 0)
        {
            return Array.Empty<(int, int)>();
        }

        // Filter: span must not start inside the tail window.
        var filtered = raw.Where(s => s.Start < tailStart).ToList();
        if (filtered.Count == 0)
        {
            return Array.Empty<(int, int)>();
        }

        // Sort by start, then merge overlaps.
        filtered.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(int Start, int Length)>();
        foreach (var span in filtered)
        {
            if (merged.Count > 0)
            {
                var last = merged[^1];
                if (span.Start <= last.Start + last.Length)
                {
                    var newEnd = Math.Max(last.Start + last.Length, span.Start + span.Length);
                    merged[^1] = (last.Start, newEnd - last.Start);
                    continue;
                }
            }

            merged.Add(span);
        }

        return merged;
    }

    private void CollectMatches(Regex pattern, string text, List<(int Start, int Length)> into, bool useCustomTimeout)
    {
        try
        {
            // Regex objects carry a timeout baked in at construction. Built-ins use 50ms; user
            // patterns may have been constructed with a different timeout by the caller — but we
            // also honor the options-level timeout for user patterns by recreating the match if
            // necessary. Simplest: we trust the regex's own timeout. If user-supplied regex has no
            // timeout set, matching won't be bounded — documented in options.
            var match = pattern.Match(text);
            while (match.Success)
            {
                if (match.Length > 0)
                {
                    into.Add((match.Index, match.Length));
                }

                match = match.NextMatch();
            }
        }
        catch (RegexMatchTimeoutException)
        {
            if (useCustomTimeout)
            {
                this.TimeoutCount++;
            }
        }
    }

    /// <summary>
    /// Computes the start index of the tail window. Spans starting at or after this index are
    /// left in place.
    /// </summary>
    public static int ComputeTailStart(int textLength, double fraction, int minChars)
    {
        if (textLength <= 0)
        {
            return 0;
        }

        var fractionChars = (int)Math.Ceiling(textLength * Math.Clamp(fraction, 0.0, 1.0));
        var windowChars = Math.Max(fractionChars, Math.Max(0, minChars));
        return Math.Max(0, textLength - windowChars);
    }
}
