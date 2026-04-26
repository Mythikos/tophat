namespace TopHat.Feedback;

/// <summary>
/// No-op <see cref="ICompressionFeedbackStore"/>. Drops all writes, returns null for all
/// reads. Use when recording is explicitly unwanted (privacy, perf paranoia, isolated test
/// environments). Opt in via <c>services.UseTopHatNoopFeedbackStore()</c>.
/// </summary>
public sealed class NullCompressionFeedbackStore : ICompressionFeedbackStore
{
	/// <inheritdoc/>
	public void RecordCompression(string? toolName)
	{
	}

	/// <inheritdoc/>
	public void RecordRetrieval(string? toolName, RetrievalKind kind)
	{
	}

	/// <inheritdoc/>
	public CompressionFeedbackStats? GetStats(string toolName) => null;

	/// <inheritdoc/>
	public void SetManualOverride(string toolName, FeedbackOverride feedbackOverride)
	{
	}

	/// <inheritdoc/>
	public void SeedStats(string toolName, long totalCompressions, long totalRetrievals, long fullRetrievals, long searchRetrievals, long budgetExhausted)
	{
	}
}
