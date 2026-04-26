namespace TopHat.Feedback;

/// <summary>
/// Classifies a retrieval event for feedback purposes. Drives the "is this tool's compressed
/// output sufficient or did the model need everything?" signal.
/// </summary>
public enum RetrievalKind
{
	/// <summary>
	/// The model called <c>tophat_retrieve</c> with an explicit <c>ids</c> filter — it knows
	/// which specific items it wants from the elided set. Indicates compression's keep-set
	/// missed something specific but the model can target it. Compatible with continued
	/// compression.
	/// </summary>
	Search = 0,

	/// <summary>
	/// The model called <c>tophat_retrieve</c> without an <c>ids</c> filter — it wants
	/// everything that was elided. Strong signal that the compressor's drop set wasn't
	/// safe to drop for this tool's outputs.
	/// </summary>
	Full = 1,

	/// <summary>
	/// CCR exhausted its iteration budget while still trying to retrieve. The model wanted
	/// more than CCR's <c>MaxIterations</c> allowed it to fetch. Even stronger signal than
	/// <see cref="Full"/> — counted alongside Full in decision logic but tracked separately
	/// so dashboards can distinguish "tool needs everything" from "CCR budget is too tight."
	/// </summary>
	BudgetExhausted = 2,
}
