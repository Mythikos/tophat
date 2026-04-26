namespace TopHat.Transforms.CacheAligner;

/// <summary>
/// Why the planner refused to produce a placement. Surfaces as the <c>reason</c> tag on
/// <c>tophat.transform.skipped</c> and in <c>CacheAlignerSkipped</c> log events.
/// </summary>
internal enum CacheAlignerSkipReason
{
    None,
    UnsupportedModel,
    BelowThreshold,
    AlreadyOptimized,
    NoSystemOrTools,
    SystemRestructureDisallowed,
    RegexTimeout,
}

/// <summary>
/// Result of <see cref="CacheAlignerBreakpointPlanner.Plan"/>. Either one or more ordered
/// breakpoints, or a skip reason. Never both.
/// </summary>
internal sealed class CacheAlignerPlan
{
    public IReadOnlyList<BreakpointKind> Breakpoints { get; init; } = Array.Empty<BreakpointKind>();

    public CacheAlignerSkipReason SkipReason { get; init; } = CacheAlignerSkipReason.None;

    public int PrefixChars { get; init; }

    public bool IsSkip => this.SkipReason != CacheAlignerSkipReason.None;

    public static CacheAlignerPlan Skip(CacheAlignerSkipReason reason) =>
        new() { SkipReason = reason };

    public static CacheAlignerPlan Place(IReadOnlyList<BreakpointKind> breakpoints, int prefixChars) =>
        new() { Breakpoints = breakpoints, PrefixChars = prefixChars };

    public static string ReasonToTag(CacheAlignerSkipReason reason) => reason switch
    {
        CacheAlignerSkipReason.UnsupportedModel => "unsupported_model",
        CacheAlignerSkipReason.BelowThreshold => "below_threshold",
        CacheAlignerSkipReason.AlreadyOptimized => "already_optimized",
        CacheAlignerSkipReason.NoSystemOrTools => "no_system_or_tools",
        CacheAlignerSkipReason.SystemRestructureDisallowed => "system_restructure_disallowed",
        CacheAlignerSkipReason.RegexTimeout => "regex_timeout",
        _ => "none",
    };
}
