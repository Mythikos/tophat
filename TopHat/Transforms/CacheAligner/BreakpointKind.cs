namespace TopHat.Transforms.CacheAligner;

/// <summary>
/// Where a cache_control marker was placed by the cache aligner. Surfaced to consumers via
/// <c>RequestTransformContext.Properties["tophat.cache_aligner.breakpoints"]</c> as the string name.
/// </summary>
internal enum BreakpointKind
{
    /// <summary>Marker on the last element of the <c>tools</c> array.</summary>
    ToolsEnd,

    /// <summary>Marker on the last element of the <c>system</c> prompt (string-wrapped or array).</summary>
    SystemEnd,

    /// <summary>Marker at the stable/dynamic boundary inside a Mode-B-restructured system prompt.</summary>
    DynamicTail,
}
