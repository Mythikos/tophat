namespace TopHat.Transforms.JsonContext;

/// <summary>
/// Configuration for the JSON context compressor transform.
/// All values port directly from headroom's SmartCrusherConfig.
/// </summary>
public sealed class JsonContextCompressorOptions
{
	/// <summary>
	/// Minimum number of items in an array before compression is considered.
	/// Arrays below this size pass through unchanged.
	/// Default: 5 (matches headroom's min_items_to_analyze).
	/// </summary>
	public int MinItemsToAnalyze { get; set; } = 5;

	/// <summary>
	/// Approximate minimum token count of a tool-result string before compression is attempted.
	/// Uses character count / 4 as a token estimate. Default: 200 tokens.
	/// </summary>
	public int MinTokensToCrush { get; set; } = 200;

	/// <summary>
	/// Standard deviations above mean used to flag numeric anomalies and change-point detection.
	/// Default: 2.0.
	/// </summary>
	public double VarianceThreshold { get; set; } = 2.0;

	/// <summary>
	/// Hard cap on items kept after compression. Null = rely entirely on adaptive K.
	/// Default: 15 (matches headroom's max_items_after_crush).
	/// </summary>
	public int? MaxItemsAfterCrush { get; set; } = 15;

	/// <summary>
	/// Fraction of the K budget reserved for preserving the leading items in the array. Reserved
	/// slots are kept unconditionally regardless of relevance score — this preserves "what's this
	/// a list of?" schema context for the LLM. Default: <c>0.05</c>, which rounds to 1 slot under
	/// typical <see cref="MaxItemsAfterCrush"/> values, leaving the rest of the budget for the
	/// scorer to fill competitively.
	/// </summary>
	/// <remarks>
	/// Headroom's original default was <c>0.30</c>, which reserves 6 of 20 slots — enough to starve
	/// the scorer on medium-sized arrays where the answer lives in the middle (empirically
	/// confirmed on the <c>paraphrase-overdue</c> fixture). The TopHat default is intentionally
	/// lower. Raise it for workloads where the first few items are reliably high-value (e.g.,
	/// chat transcripts with a system-like prompt in slot 0).
	/// </remarks>
	public double FirstFraction { get; set; } = 0.05;

	/// <summary>
	/// Fraction of the K budget reserved for preserving the trailing items in the array.
	/// Reserved slots are kept unconditionally regardless of relevance score — this preserves
	/// "what was the latest entry?" context. Default: <c>0.05</c>, which rounds to 1 slot under
	/// typical <see cref="MaxItemsAfterCrush"/> values.
	/// </summary>
	public double LastFraction { get; set; } = 0.05;

	/// <summary>
	/// Whether to detect and preserve array elements adjacent to change points.
	/// Default: true.
	/// </summary>
	public bool PreserveChangePoints { get; set; } = true;

	/// <summary>
	/// Number of messages from the end of the conversation to consider "non-frozen".
	/// Messages before this count may be in the provider's prefix cache and are skipped.
	/// Default: 2 (covers the typical prefix-cache span of 2 recent turns).
	/// </summary>
	/// <remarks>
	/// This differs from headroom's explicit frozen_message_count: headroom gets the exact
	/// count from the provider, while TopHat uses this heuristic. Misconfiguration can
	/// cause over-compression of cached messages.
	/// </remarks>
	public int UnfrozenMessageCount { get; set; } = 2;
}
