namespace TopHat.Feedback;

/// <summary>
/// Declarative per-tool feedback overrides registered at app startup via
/// <c>UseTopHatFeedbackOverrides()</c>. Read by the compressor at request time alongside
/// store-based runtime overrides.
/// </summary>
/// <remarks>
/// <para>
/// Precedence: store-based runtime override (set via <see cref="ICompressionFeedbackStore.SetManualOverride"/>)
/// wins when present. This config fills in when the store has no explicit override for a tool.
/// Lets users deploy with static "always skip these tools" rules while retaining the ability
/// to flip individual tools dynamically via runtime API.
/// </para>
/// <para>
/// Distinct from the <see cref="FeedbackThresholds"/> empirical layer — config overrides apply
/// regardless of whether <c>UseTopHatFeedbackDecisions()</c> was called, since they're
/// user-declared truth not learned signal.
/// </para>
/// </remarks>
public sealed class FeedbackOverridesConfiguration
{
	private readonly Dictionary<string, FeedbackOverride> _overrides = new(StringComparer.Ordinal);

	/// <summary>
	/// All declared overrides. Read-only view; mutate via <see cref="SkipCompressionFor"/> /
	/// <see cref="AlwaysCompressFor"/>.
	/// </summary>
	public IReadOnlyDictionary<string, FeedbackOverride> Overrides => this._overrides;

	/// <summary>
	/// Marks <paramref name="toolName"/> as never-compress. Passing through TopHat does no
	/// transform on this tool's outputs.
	/// </summary>
	public FeedbackOverridesConfiguration SkipCompressionFor(string toolName)
	{
		ArgumentException.ThrowIfNullOrEmpty(toolName);
		this._overrides[toolName] = FeedbackOverride.SkipCompression;
		return this;
	}

	/// <summary>
	/// Marks <paramref name="toolName"/> as always-compress. Useful when learned signal would
	/// otherwise (incorrectly) recommend skipping — e.g., a tool whose initial samples were
	/// pathological but is known to be safe to compress.
	/// </summary>
	public FeedbackOverridesConfiguration AlwaysCompressFor(string toolName)
	{
		ArgumentException.ThrowIfNullOrEmpty(toolName);
		this._overrides[toolName] = FeedbackOverride.AlwaysCompress;
		return this;
	}

	/// <summary>
	/// Retrieves the configured override for <paramref name="toolName"/>, or
	/// <see cref="FeedbackOverride.None"/> if no entry exists.
	/// </summary>
	public FeedbackOverride GetOverride(string toolName)
	{
		ArgumentException.ThrowIfNullOrEmpty(toolName);
		return this._overrides.TryGetValue(toolName, out var value) ? value : FeedbackOverride.None;
	}
}
