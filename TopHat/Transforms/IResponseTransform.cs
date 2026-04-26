namespace TopHat.Transforms;

/// <summary>
/// Response-side observation hook. Wired end-to-end as of M5 (observation-only). Implementations
/// receive a <see cref="ResponseTransformContext"/> carrying the parsed JSON body for non-streaming
/// responses (<c>TeeMode.WholeBody</c>), a list of usage-bearing SSE frames for streaming responses
/// (<c>TeeMode.Sse</c>), or neither for opaque content types (<c>TeeMode.Passthrough</c>).
/// </summary>
/// <remarks>
/// <para><b>Observation-only</b>: the context exposes only getters. Transforms cannot change what
/// reaches the caller. Mutating response transforms are reserved for a future
/// <c>IMutatingResponseTransform</c> interface.</para>
/// <para><b>Dispatch timing</b>: transforms fire ONCE per response, inside the tee's async
/// finalization path. If the response stream is synchronously disposed (never walked
/// <c>ReadAsync</c> / <c>DisposeAsync</c>), response transforms DO NOT fire. Consumers who register
/// response transforms MUST use <c>await using</c> or await a body-read method on the response.</para>
/// </remarks>
public interface IResponseTransform
{
    ValueTask InvokeAsync(ResponseTransformContext context, CancellationToken cancellationToken);
}
