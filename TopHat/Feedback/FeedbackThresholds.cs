namespace TopHat.Feedback;

/// <summary>
/// Tunable thresholds for the feedback decision layer. Defaults match headroom's proven
/// values; adjust via <c>UseTopHatFeedbackDecisions(opt =&gt; ...)</c>.
/// </summary>
public sealed class FeedbackThresholds
{
	/// <summary>
	/// True only when <c>UseTopHatFeedbackDecisions()</c> has been called. Distinguishes the
	/// default-constructed <c>IOptions&lt;FeedbackThresholds&gt;</c> instance the DI container
	/// hands back automatically (decisions OFF) from one that was explicitly configured by
	/// the user (decisions ON). The compressor checks this before consulting the store.
	/// </summary>
	public bool Enabled { get; set; }

	/// <summary>
	/// Minimum compression events for a tool before the decision layer makes any
	/// recommendation. Below this, compression proceeds as standard. Cold-start ceiling.
	/// Default: 5 (matches headroom).
	/// </summary>
	public int MinSamplesForHints { get; set; } = 5;

	/// <summary>
	/// Retrieval rate above which compression is considered "too aggressive" and the
	/// decision layer starts intervening. Range [0, 1]. Default: 0.5 (matches headroom) —
	/// >50% of compressions triggering retrieval is the boundary.
	/// </summary>
	public double HighRetrievalThreshold { get; set; } = 0.5;

	/// <summary>
	/// Full-retrieval rate above which compression is recommended to be skipped entirely
	/// for a tool whose <see cref="HighRetrievalThreshold"/> is also exceeded. When the
	/// model nearly always asks for everything, compressing only adds CCR overhead.
	/// Range [0, 1]. Default: 0.8 (matches headroom).
	/// </summary>
	public double FullRetrievalThreshold { get; set; } = 0.8;
}
