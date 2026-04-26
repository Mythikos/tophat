namespace TopHat.Streaming;

/// <summary>
/// Response-observation mode selected from the upstream <c>Content-Type</c>. Surfaced on
/// <see cref="Transforms.ResponseTransformContext.Mode"/> so transforms can know the shape of the
/// data available to them (parsed body vs. SSE frames vs. nothing).
/// </summary>
public enum TeeMode
{
    /// <summary>Response forwarded unchanged; no observation.</summary>
    Passthrough,

    /// <summary>SSE response; parser frames on \n\n and extracts usage events.</summary>
    Sse,

    /// <summary>JSON response; whole body accumulated (capped) and parsed once at close.</summary>
    WholeBody,
}
