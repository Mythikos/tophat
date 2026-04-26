namespace TopHat.Transforms.JsonContext.Summarization;

/// <summary>
/// Options controlling <see cref="NumericFieldSummarizer"/> behavior.
/// </summary>
public sealed class NumericFieldSummarizerOptions
{
	/// <summary>Maximum number of numeric fields reported. Default: 5.</summary>
	public int MaxFields { get; set; } = 5;

	/// <summary>Minimum number of numeric samples required before a field is summarized. Default: 5.</summary>
	public int MinSampleSize { get; set; } = 5;

	/// <summary>Whether to include p50, p90, and p99 percentiles in the summary. Default: true.</summary>
	public bool IncludePercentiles { get; set; } = true;

	/// <summary>
	/// If set, restricts summarization to these field names (applied after <see cref="FieldDenyList"/>).
	/// Null = all numeric fields eligible.
	/// </summary>
	public string[]? FieldAllowList { get; set; }

	/// <summary>
	/// Field names excluded from summarization — typically id-like or timestamp fields whose stats
	/// aren't informative. Matched case-insensitively.
	/// </summary>
	public string[] FieldDenyList { get; set; } = new[]
	{
		"id", "index", "idx", "timestamp", "ts", "created_at", "updated_at", "epoch",
	};
}
