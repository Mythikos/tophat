namespace TopHat.Relevance;

/// <summary>
/// Fuses multiple <see cref="IRelevanceScorer"/> implementations by summing each scorer's
/// batch-local min-max-normalized output. Returned by <c>JsonContextCompressorTransform</c>
/// when two or more scorers are registered via DI; callers normally never construct it directly.
/// </summary>
/// <remarks>
/// <para>
/// Algorithm per batch:
/// <list type="number">
/// <item><description>Each inner scorer scores the batch independently, producing values in <c>[0, 1]</c>.</description></item>
/// <item><description>Each scorer's outputs are min-max normalized within this batch so the scorer's strongest item becomes 1.0 and its weakest becomes 0.0. A scorer that produces an all-equal output (no signal on this query) contributes 0.0 for every item — its noise is filtered out rather than dragging the fusion down.</description></item>
/// <item><description>Per-item fused score = sum of normalized scores across inner scorers, divided by the scorer count to stay in <c>[0, 1]</c>.</description></item>
/// <item><description>Matched terms are the union of every inner scorer's matched terms for that item.</description></item>
/// </list>
/// </para>
/// <para>
/// Chosen over Reciprocal Rank Fusion because the top-K cutoff shape of the compressor wants
/// each scorer to act as an independent "safety net" rather than a rank-voter that can be
/// outvoted. A scorer that strongly identifies an item rewards that item at normalized 1.0
/// regardless of whether the other scorers found it — the same item would have been dragged
/// down under RRF if the other scorers ranked it low.
/// </para>
/// </remarks>
public sealed class FusedRelevanceScorer : IRelevanceScorer
{
	private readonly IReadOnlyList<IRelevanceScorer> _scorers;

	public FusedRelevanceScorer(IReadOnlyList<IRelevanceScorer> scorers)
	{
		ArgumentNullException.ThrowIfNull(scorers);

		if (scorers.Count == 0)
		{
			throw new ArgumentException("FusedRelevanceScorer requires at least one inner scorer.", nameof(scorers));
		}

		_scorers = scorers;
	}

	/// <summary>Count of inner scorers being fused. Surfaced for diagnostics/logging.</summary>
	public int ScorerCount => _scorers.Count;

	/// <inheritdoc/>
	public RelevanceScore Score(string item, string context)
	{
		return ScoreBatch(new[] { item }, context)[0];
	}

	/// <inheritdoc/>
	public IReadOnlyList<RelevanceScore> ScoreBatch(IReadOnlyList<string> items, string context)
	{
		ArgumentNullException.ThrowIfNull(items);

		if (items.Count == 0)
		{
			return Array.Empty<RelevanceScore>();
		}

		var fused = new double[items.Count];
		var mergedMatches = new HashSet<string>[items.Count];

		for (var i = 0; i < items.Count; i++)
		{
			mergedMatches[i] = new HashSet<string>(StringComparer.Ordinal);
		}

		foreach (var scorer in _scorers)
		{
			var batchScores = scorer.ScoreBatch(items, context);

			// Determine this scorer's min/max on this batch to define the normalization range.
			var batchMin = double.MaxValue;
			var batchMax = double.MinValue;

			for (var i = 0; i < items.Count; i++)
			{
				var raw = batchScores[i].Score;
				if (raw < batchMin)
				{
					batchMin = raw;
				}
				if (raw > batchMax)
				{
					batchMax = raw;
				}
			}

			// A flat distribution means the scorer produced no signal on this query — all items tied.
			// Contribute 0.0 so its noise doesn't dilute another scorer that actually has signal.
			var range = batchMax - batchMin;

			for (var i = 0; i < items.Count; i++)
			{
				var raw = batchScores[i].Score;
				var normalized = range > 0 ? (raw - batchMin) / range : 0.0;
				fused[i] += normalized;

				foreach (var term in batchScores[i].MatchedTerms)
				{
					mergedMatches[i].Add(term);
				}
			}
		}

		// Divide by scorer count so the fused value stays within the [0, 1] RelevanceScore contract.
		var results = new RelevanceScore[items.Count];
		var scorerCount = _scorers.Count;

		for (var i = 0; i < items.Count; i++)
		{
			var normalizedSum = fused[i] / scorerCount;
			var reason = $"Fused({scorerCount}): {normalizedSum:F3}";
			results[i] = new RelevanceScore(normalizedSum, reason, mergedMatches[i].ToArray());
		}

		return results;
	}
}
