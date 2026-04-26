namespace TopHat.Relevance;

/// <summary>
/// Scores items against a query context to determine which tool-result entries are most
/// relevant to the current conversation. Used by <c>JsonContextCompressorTransform</c>
/// to decide which items to preserve during compression.
/// </summary>
/// <remarks>
/// Implementations ship as opt-in packages: <c>TopHat.Relevance.BM25</c> for keyword scoring
/// (zero external dependencies, excellent for exact UUID/ID matching) and
/// <c>TopHat.Relevance.Onnx</c> for semantic scoring. Register one or both; when multiple
/// scorers are in the DI pool they are automatically fused via <see cref="FusedRelevanceScorer"/>.
/// </remarks>
public interface IRelevanceScorer
{
	/// <summary>
	/// Scores a single item string against the query context.
	/// </summary>
	/// <param name="item">The item text to score (typically a serialized JSON object or string).</param>
	/// <param name="context">Query context derived from recent conversation messages.</param>
	/// <returns>A <see cref="RelevanceScore"/> with a score in [0.0, 1.0].</returns>
	RelevanceScore Score(string item, string context);

	/// <summary>
	/// Scores a batch of items against the query context. Implementations may use average
	/// document length across the batch for better BM25 normalization.
	/// </summary>
	/// <param name="items">Items to score.</param>
	/// <param name="context">Query context derived from recent conversation messages.</param>
	/// <returns>Scores in the same order as <paramref name="items"/>.</returns>
	IReadOnlyList<RelevanceScore> ScoreBatch(IReadOnlyList<string> items, string context);
}
