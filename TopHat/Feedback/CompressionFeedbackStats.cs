namespace TopHat.Feedback;

/// <summary>
/// Per-tool feedback statistics. Read-mostly snapshot of what the store has accumulated for
/// a specific tool name. Counters monotonically increase across the store's lifetime;
/// derived rates are computed at read time.
/// </summary>
/// <param name="ToolName">The tool name these stats apply to (e.g., <c>get_logs</c>).</param>
/// <param name="TotalCompressions">Number of times this tool's outputs were compressed.</param>
/// <param name="TotalRetrievals">Total <c>tophat_retrieve</c> calls for this tool. Sums Full + Search + BudgetExhausted.</param>
/// <param name="FullRetrievals">Retrievals where the model asked for everything (no <c>ids</c> filter).</param>
/// <param name="SearchRetrievals">Retrievals targeting specific <c>ids</c>.</param>
/// <param name="BudgetExhausted">CCR iteration-budget hits. Strongest "tool needed everything" signal.</param>
/// <param name="ManualOverride">User-declared override that takes precedence over learned stats.</param>
/// <param name="LastEventUtc">Timestamp of the most recent compression or retrieval event.</param>
public sealed record CompressionFeedbackStats(
	string ToolName,
	long TotalCompressions,
	long TotalRetrievals,
	long FullRetrievals,
	long SearchRetrievals,
	long BudgetExhausted,
	FeedbackOverride ManualOverride,
	DateTimeOffset? LastEventUtc)
{
	/// <summary>
	/// Fraction of compressions that triggered any retrieval. High values mean compression
	/// is too aggressive for this tool — the model keeps reaching for elided items.
	/// </summary>
	public double RetrievalRate => this.TotalCompressions == 0
		? 0.0
		: (double)this.TotalRetrievals / this.TotalCompressions;

	/// <summary>
	/// Fraction of retrievals that asked for everything (Full + BudgetExhausted) versus
	/// targeted searches. High values mean the tool's outputs are typically needed in full
	/// — compression is providing little value and may be costing more via CCR overhead.
	/// </summary>
	public double FullRetrievalRate => this.TotalRetrievals == 0
		? 0.0
		: (double)(this.FullRetrievals + this.BudgetExhausted) / this.TotalRetrievals;
}
