namespace TopHat.Relevance;

/// <summary>
/// The result of scoring a single item against a query context.
/// </summary>
public sealed record RelevanceScore
{
	/// <summary>
	/// Relevance score in the range [0.0, 1.0]. Higher means more relevant.
	/// </summary>
	public double Score { get; init; }

	/// <summary>
	/// Human-readable explanation of why this score was assigned.
	/// </summary>
	public string Reason { get; init; } = string.Empty;

	/// <summary>
	/// The query terms that were found in the item, contributing to the score.
	/// </summary>
	public IReadOnlyList<string> MatchedTerms { get; init; } = [];

	public RelevanceScore(double score, string reason = "", IReadOnlyList<string>? matchedTerms = null)
	{
		Score = Math.Clamp(score, 0.0, 1.0);
		Reason = reason;
		MatchedTerms = matchedTerms ?? [];
	}
}
