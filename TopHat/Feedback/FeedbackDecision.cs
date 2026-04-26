namespace TopHat.Feedback;

/// <summary>
/// Pure-function decision logic that maps accumulated <see cref="CompressionFeedbackStats"/>
/// + <see cref="FeedbackThresholds"/> to a <see cref="CompressionGuidance"/>. No I/O, no
/// state — easy to unit-test the boundary conditions.
/// </summary>
/// <remarks>
/// Three-tier evaluation order:
/// <list type="number">
///   <item><description><see cref="FeedbackOverride"/> on the stats — user-declared decisions
///   take absolute precedence over learned data.</description></item>
///   <item><description>Sample count below <see cref="FeedbackThresholds.MinSamplesForHints"/>
///   — cold start, return Standard.</description></item>
///   <item><description>Retrieval-rate thresholds — see <see cref="FeedbackThresholds"/>.</description></item>
/// </list>
/// </remarks>
public static class FeedbackDecision
{
	/// <summary>
	/// Returns the guidance for a tool. <paramref name="stats"/> may be null when the store
	/// has no record for the tool yet — in that case Standard is returned (cold start).
	/// </summary>
	public static CompressionGuidance Decide(CompressionFeedbackStats? stats, FeedbackThresholds thresholds)
	{
		ArgumentNullException.ThrowIfNull(thresholds);

		if (stats is null)
		{
			return CompressionGuidance.Standard("no feedback data");
		}

		// Manual override always wins.
		if (stats.ManualOverride == FeedbackOverride.SkipCompression)
		{
			return CompressionGuidance.Skip("manual override: skip");
		}
		if (stats.ManualOverride == FeedbackOverride.AlwaysCompress)
		{
			return CompressionGuidance.Standard("manual override: always compress");
		}

		// Cold start.
		if (stats.TotalCompressions < thresholds.MinSamplesForHints)
		{
			return CompressionGuidance.Standard($"cold start ({stats.TotalCompressions}/{thresholds.MinSamplesForHints} samples)");
		}

		// High retrieval rate AND high full-retrieval rate → tool data is always needed in full,
		// compression provides no value here and adds CCR overhead.
		if (stats.RetrievalRate > thresholds.HighRetrievalThreshold
			&& stats.FullRetrievalRate > thresholds.FullRetrievalThreshold)
		{
			return CompressionGuidance.Skip(
				$"retrieval={stats.RetrievalRate:P0}, full={stats.FullRetrievalRate:P0} — tool needs full data");
		}

		return CompressionGuidance.Standard(
			$"retrieval={stats.RetrievalRate:P0} below skip threshold");
	}
}
