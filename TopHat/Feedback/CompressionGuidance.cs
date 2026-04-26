namespace TopHat.Feedback;

/// <summary>
/// Output of <see cref="FeedbackDecision.Decide"/>. Tells the compressor what to do for a
/// specific tool's outputs given accumulated stats and configured thresholds.
/// </summary>
/// <param name="SkipCompression">True if the compressor should leave the tool_result content
/// unchanged for this tool. Implies no CCR registration either.</param>
/// <param name="Reason">Human-readable explanation of why this guidance was produced —
/// useful for logging and dashboards. Always populated.</param>
public sealed record CompressionGuidance(bool SkipCompression, string Reason)
{
	/// <summary>"Compress as standard" — no override, no skip.</summary>
	public static CompressionGuidance Standard(string reason) => new(SkipCompression: false, reason);

	/// <summary>"Don't compress this tool's outputs."</summary>
	public static CompressionGuidance Skip(string reason) => new(SkipCompression: true, reason);
}
