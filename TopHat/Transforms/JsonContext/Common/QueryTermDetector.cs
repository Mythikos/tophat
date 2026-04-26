using System.Text.RegularExpressions;

namespace TopHat.Transforms.JsonContext.Common;

/// <summary>
/// Extracts significant literal tokens from a query string and finds items that contain any of them.
/// Used to guarantee preservation of clearly-matching items during compression when BM25 ranking
/// would otherwise drop them (e.g., length-normalized BM25 penalizes a slightly-longer snippet
/// despite it containing the exact query identifier).
/// </summary>
/// <remarks>
/// Only preserves items for terms whose match count falls below the compression budget. This
/// keeps the rule from forcing all-item preservation when the query mentions a common field name
/// (e.g., "how many requests had latency_ms exceeding 500" — latency_ms appears in every record).
/// </remarks>
internal static class QueryTermDetector
{
	private const int MinTokenLength = 4;

	// Single/double/backtick quoted content. Non-greedy to stop at the first matching closer.
	private static readonly Regex s_quotedPattern = new (
		@"'([^']{4,})'|""([^""]{4,})""|`([^`]{4,})`",
		RegexOptions.Compiled);

	// camelCase identifier: at least one lowercase-then-uppercase transition.
	private static readonly Regex s_camelCasePattern = new (
		@"\b[a-z]+[A-Z][a-zA-Z0-9]*\b",
		RegexOptions.Compiled);

	// snake_case identifier: one-or-more alphanumeric, underscore, more alphanumeric.
	private static readonly Regex s_snakeCasePattern = new (
		@"\b[a-zA-Z][a-zA-Z0-9]*_[a-zA-Z0-9_]+\b",
		RegexOptions.Compiled);

	// Dotted / path-like identifier (e.g. package.json, src/auth/token.ts).
	private static readonly Regex s_pathLikePattern = new (
		@"\b[a-zA-Z0-9]+(?:[./][a-zA-Z0-9_-]+)+\b",
		RegexOptions.Compiled);

	/// <summary>
	/// Parses <paramref name="queryContext"/> and returns distinct case-folded tokens considered
	/// high-signal for exact-match preservation. Returns an empty array if none found.
	/// </summary>
	public static string[] ExtractTerms(string queryContext)
	{
		if (string.IsNullOrWhiteSpace(queryContext))
		{
			return Array.Empty<string>();
		}

		var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		AddQuotedMatches(queryContext, terms);
		AddRegexMatches(queryContext, s_camelCasePattern, terms);
		AddRegexMatches(queryContext, s_snakeCasePattern, terms);
		AddRegexMatches(queryContext, s_pathLikePattern, terms);

		return terms.Count == 0 ? Array.Empty<string>() : terms.ToArray();
	}

	/// <summary>
	/// Given <paramref name="itemStrings"/> and extracted <paramref name="terms"/>, returns the set
	/// of item indices whose text contains any term — but only for terms whose match count does
	/// not exceed <paramref name="maxMatchesPerTerm"/>. Terms matching too broadly (e.g., a field
	/// name appearing in every record) are ignored entirely.
	/// </summary>
	public static HashSet<int> FindPreservedIndices(IReadOnlyList<string> itemStrings, IReadOnlyList<string> terms, int maxMatchesPerTerm)
	{
		var preserved = new HashSet<int>();

		if (terms.Count == 0 || itemStrings.Count == 0 || maxMatchesPerTerm <= 0)
		{
			return preserved;
		}

		foreach (var term in terms)
		{
			var matches = new List<int>();

			for (var idx = 0; idx < itemStrings.Count; idx++)
			{
				if (itemStrings[idx].Contains(term, StringComparison.OrdinalIgnoreCase))
				{
					matches.Add(idx);

					if (matches.Count > maxMatchesPerTerm)
					{
						// Term is too broad (e.g., common field name) — drop it entirely.
						break;
					}
				}
			}

			if (matches.Count > 0 && matches.Count <= maxMatchesPerTerm)
			{
				foreach (var idx in matches)
				{
					preserved.Add(idx);
				}
			}
		}

		return preserved;
	}

	private static void AddQuotedMatches(string text, HashSet<string> accumulator)
	{
		foreach (Match match in s_quotedPattern.Matches(text))
		{
			for (var group = 1; group < match.Groups.Count; group++)
			{
				var value = match.Groups[group].Value;

				if (value.Length >= MinTokenLength)
				{
					accumulator.Add(value);
				}
			}
		}
	}

	private static void AddRegexMatches(string text, Regex pattern, HashSet<string> accumulator)
	{
		foreach (Match match in pattern.Matches(text))
		{
			var value = match.Value;

			if (value.Length >= MinTokenLength)
			{
				accumulator.Add(value);
			}
		}
	}
}
