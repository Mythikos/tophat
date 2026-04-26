namespace TopHat.Transforms;

/// <summary>
/// How the transform pipeline reacts when a transform throws.
/// </summary>
public enum TransformFailureMode
{
    /// <summary>
    /// Default. On failure: log Warning, increment <c>tophat.transform.errors</c>, re-parse the body
    /// from the buffered snapshot so the next transform sees pristine state, continue the pipeline.
    /// Consumer's request still succeeds even if an optimization transform is broken.
    /// </summary>
    FailOpen = 0,

    /// <summary>
    /// On failure: surface as an upstream error to the caller. The upstream request is NOT sent.
    /// Use for transforms whose partial application is worse than not running at all.
    /// </summary>
    FailClosed = 1,

    /// <summary>
    /// Reserved for a future implementation that trips between FailOpen and a disabled-transform
    /// state after N failures in a time window. <b>Selecting this mode in M3 throws
    /// <see cref="NotImplementedException"/> at service-provider build time.</b>
    /// </summary>
    CircuitBreaker = 2,
}
