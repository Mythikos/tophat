namespace TopHat.Feedback;

/// <summary>
/// User-declared override for how to handle a specific tool. Takes precedence over learned
/// statistics — useful for cold-start tuning where the user already knows a tool's data
/// shape and doesn't want to wait for empirical convergence.
/// </summary>
public enum FeedbackOverride
{
	/// <summary>
	/// No override — let the decision layer apply learned statistics (or compress as standard
	/// if there aren't enough samples yet).
	/// </summary>
	None = 0,

	/// <summary>
	/// Skip compression entirely for this tool, regardless of statistics. Use when the tool's
	/// outputs are known to always need full inspection (aggregations, full scans, billing
	/// reconciliation, etc.).
	/// </summary>
	SkipCompression = 1,

	/// <summary>
	/// Always compress this tool's outputs, regardless of statistics. Use when learned
	/// statistics are misleading — e.g., a tool whose outputs were rarely retrieved during
	/// initial samples but is known to be safe to compress aggressively.
	/// </summary>
	AlwaysCompress = 2,
}
