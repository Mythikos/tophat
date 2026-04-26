namespace TopHat.Feedback;

/// <summary>
/// Records compression and retrieval events per-tool, maintaining counters that drive the
/// "should we compress this tool's outputs?" decision once enough samples accumulate. Single
/// store per process — registered as a singleton; only one is active at a time.
/// </summary>
/// <remarks>
/// <para>
/// Recording happens automatically when a store is registered. The decision layer that
/// CONSULTS these stats is opt-in via <c>UseTopHatFeedbackDecisions()</c>; without that
/// extension, the store accumulates data for observability but doesn't change compression
/// behavior. This separation lets users gather signal before flipping the behavior switch.
/// </para>
/// <para>
/// Implementations must be thread-safe — record events fire concurrently from any request
/// the application is processing. <see cref="GetStats"/> may be called on any thread.
/// </para>
/// </remarks>
public interface ICompressionFeedbackStore
{
	/// <summary>
	/// Records a compression event. Called once per tool_result that the compressor
	/// successfully shrunk. Increments <see cref="CompressionFeedbackStats.TotalCompressions"/>
	/// for <paramref name="toolName"/>.
	/// </summary>
	/// <param name="toolName">The upstream tool's name (e.g., <c>get_logs</c>). If null or
	/// empty, the implementation may either skip recording or use a sentinel; current
	/// implementations skip.</param>
	void RecordCompression(string? toolName);

	/// <summary>
	/// Records a retrieval event — fires when CCR fulfils a <c>tophat_retrieve</c> call,
	/// or when CCR exhausts its iteration budget. Increments the appropriate counter on
	/// <paramref name="toolName"/>'s stats.
	/// </summary>
	void RecordRetrieval(string? toolName, RetrievalKind kind);

	/// <summary>
	/// Returns a snapshot of the stats for <paramref name="toolName"/>, or <c>null</c> if
	/// no events have been recorded for it. Subsequent reads return fresh snapshots.
	/// </summary>
	CompressionFeedbackStats? GetStats(string toolName);

	/// <summary>
	/// Sets a user-declared override for <paramref name="toolName"/>. Takes precedence over
	/// learned statistics in the decision layer. Pass <see cref="FeedbackOverride.None"/>
	/// to clear an existing override.
	/// </summary>
	void SetManualOverride(string toolName, FeedbackOverride feedbackOverride);

	/// <summary>
	/// Pre-populates statistics for <paramref name="toolName"/>. Useful when the user has
	/// prior knowledge (e.g., from offline measurement) and wants to skip cold-start.
	/// Future events accumulate on top of the seed; the seed is not a manual override.
	/// </summary>
	void SeedStats(string toolName, long totalCompressions, long totalRetrievals, long fullRetrievals, long searchRetrievals, long budgetExhausted);
}
