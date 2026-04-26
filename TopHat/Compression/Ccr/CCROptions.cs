namespace TopHat.Compression.CCR;

/// <summary>
/// Configuration for Compression Context Retrieval — the mechanism that lets a model request
/// items elided by the JSON context compressor via a synthetic <c>tophat_retrieve</c> tool call.
/// </summary>
public sealed class CCROptions
{
	/// <summary>
	/// How long a compressed tool_result's dropped items are retained in the store before TTL
	/// eviction. Retrieval attempts against expired keys return an empty list. Default: 1 hour.
	/// </summary>
	/// <remarks>
	/// CCR is intended as a same-turn / next-few-turns rescue valve, not a durable state channel.
	/// A shorter TTL trades memory for retrievability; a longer TTL accommodates long-running
	/// conversations where the model may circle back to earlier tool_results.
	/// </remarks>
	public TimeSpan RetentionDuration { get; set; } = TimeSpan.FromHours(1);

	/// <summary>
	/// Maximum number of retrieval round-trips the orchestrator will make for a single client
	/// request. Each round-trip is an extra upstream call. When the cap is hit, the orchestrator
	/// terminates the loop by injecting a synthetic tool_result noting the budget exhaustion and
	/// issues one final completion. Default: 3.
	/// </summary>
	/// <remarks>
	/// Each iteration costs an additional upstream call (billed as input-token reprocessing), so
	/// the cap is both a safety check against pathological models and a cost ceiling. Raise it
	/// for workloads where multi-step retrieval is expected; lower it to 1 if you want a single
	/// rescue opportunity and nothing more.
	/// </remarks>
	public int MaxIterations { get; set; } = 3;

	/// <summary>
	/// Hard cap on how many items a single retrieval call can pull back, regardless of what the
	/// model requested in the <c>limit</c> field. Default: 50. Exists to prevent a malicious or
	/// confused model from ballooning the follow-up payload past any useful size.
	/// </summary>
	public int RetrievalItemCeiling { get; set; } = 50;
}
