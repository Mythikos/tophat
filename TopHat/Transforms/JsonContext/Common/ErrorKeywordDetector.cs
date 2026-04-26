namespace TopHat.Transforms.JsonContext.Common;

/// <summary>
/// Detects error-bearing tool-result items by checking for known error keywords.
/// Items that contain at least one keyword are always preserved during compression.
/// Port of headroom's error_detection.py ERROR_KEYWORDS frozenset.
/// </summary>
internal static class ErrorKeywordDetector
{
	// Exact set from headroom error_detection.py — case-insensitive substring match.
	private static readonly HashSet<string> s_keywords = new(StringComparer.OrdinalIgnoreCase)
	{
		"error", "exception", "failed", "failure", "critical",
		"fatal", "crash", "panic", "abort", "timeout", "denied", "rejected",
	};

	/// <summary>
	/// Returns true if <paramref name="text"/> contains any error keyword as a substring.
	/// </summary>
	public static bool ContainsErrorKeyword(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return false;
		}

		foreach (var keyword in s_keywords)
		{
			if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Returns the first error keyword found in <paramref name="text"/>, or null if none.
	/// </summary>
	public static string? FirstMatch(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return null;
		}

		foreach (var keyword in s_keywords)
		{
			if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
			{
				return keyword;
			}
		}

		return null;
	}
}
