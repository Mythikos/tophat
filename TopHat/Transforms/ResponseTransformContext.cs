using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using TopHat.Providers;
using TopHat.Streaming;

namespace TopHat.Transforms;

/// <summary>
/// Per-response state exposed to an <see cref="IResponseTransform"/>. Observation-only: all
/// properties are read-only snapshots and there is no mutation hook. Transforms cannot change
/// what the caller sees — that capability is reserved for a future <c>IMutatingResponseTransform</c>.
/// </summary>
/// <remarks>
/// <para>Transforms are dispatched ONCE per response, inside the tee's async finalization path
/// (EOF or async disposal). If the response stream is synchronously disposed, response transforms
/// DO NOT fire; callers who register response transforms must use <c>await using</c> or a
/// <c>ReadAsStreamAsync</c>/<c>ReadAsByteArrayAsync</c> path to trigger async finalization.</para>
/// <para><see cref="Body"/> is populated iff <see cref="Mode"/> is <see cref="TeeMode.WholeBody"/>
/// and the response body parsed successfully under the configured cap.
/// <see cref="ObservedEvents"/> is populated iff <see cref="Mode"/> is <see cref="TeeMode.Sse"/>,
/// and contains only usage-bearing frames — non-usage frames are counted in
/// <see cref="ObservedEventCount"/> but not retained.</para>
/// </remarks>
public sealed class ResponseTransformContext
{
    internal ResponseTransformContext(
        TopHatProviderKind provider,
        TopHatTarget target,
        string model,
        string localId,
        int statusCode,
        HttpResponseHeaders headers,
        TeeMode mode,
        JsonNode? body,
        IReadOnlyList<SseObservation>? observedEvents,
        int observedEventCount,
        bool truncatedObservedEvents,
        ILogger logger,
        IDictionary<string, object?> properties,
        CancellationToken cancellationToken)
    {
        this.Provider = provider;
        this.Target = target;
        this.Model = model;
        this.LocalId = localId;
        this.StatusCode = statusCode;
        this.Headers = headers;
        this.Mode = mode;
        this.Body = body;
        this.ObservedEvents = observedEvents;
        this.ObservedEventCount = observedEventCount;
        this.TruncatedObservedEvents = truncatedObservedEvents;
        this.CancellationToken = cancellationToken;
        this.Logger = logger;
        this.Properties = properties;
    }

    /// <summary>Provider classification for the request this response is for.</summary>
    public TopHatProviderKind Provider { get; }

    /// <summary>Target classification for the request this response is for.</summary>
    public TopHatTarget Target { get; }

    /// <summary>
    /// Model name extracted from the original request body (or the sentinel <c>"unknown"</c>).
    /// </summary>
    public string Model { get; }

    /// <summary>
    /// Local correlation ID matching the originating request. Use this to correlate with
    /// request-side metrics and logs.
    /// </summary>
    public string LocalId { get; }

    /// <summary>HTTP status code of the upstream response.</summary>
    public int StatusCode { get; }

    /// <summary>Upstream response headers (live reference; the framework owns lifetime).</summary>
    public HttpResponseHeaders Headers { get; }

    /// <summary>Tee mode selected for this response: Passthrough, Sse, or WholeBody.</summary>
    public TeeMode Mode { get; }

    /// <summary>
    /// Parsed response body for <see cref="TeeMode.WholeBody"/> responses under cap. Null in all
    /// other cases (Passthrough, Sse, over-cap, parse failure, null content).
    /// </summary>
    public JsonNode? Body { get; }

    /// <summary>
    /// Usage-bearing SSE frames observed during streaming. Populated only for
    /// <see cref="TeeMode.Sse"/>. Non-usage frames are counted in <see cref="ObservedEventCount"/>
    /// but not retained here.
    /// </summary>
    public IReadOnlyList<SseObservation>? ObservedEvents { get; }

    /// <summary>
    /// Total count of SSE frames observed (usage + non-usage). <b>Zero is ambiguous</b>: it means
    /// either <see cref="Mode"/> is not Sse, or Sse mode with no events yet / ever. Check
    /// <c>Mode == TeeMode.Sse</c> first to disambiguate.
    /// </summary>
    public int ObservedEventCount { get; }

    /// <summary>
    /// True iff the 1024-entry usage-event retention cap fired during streaming. Because usage
    /// frames per response are ≤~3 in practice, this is effectively never true — but if it flips,
    /// downstream consumers should treat the retained <see cref="ObservedEvents"/> as potentially
    /// incomplete and avoid deriving exact totals from the list alone.
    /// </summary>
    public bool TruncatedObservedEvents { get; }

    /// <summary>
    /// The original request's cancellation token. Observation-only transforms that do I/O should
    /// honor it; long-running work risks deadlocking stream finalization if not bounded.
    /// </summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// Logger scoped to this response-transform invocation. <c>{LocalId}</c> and
    /// <c>{TransformName}</c> are pushed to the scope by the pipeline.
    /// </summary>
    public ILogger Logger { get; }

    /// <summary>
    /// Scratch dictionary for transforms to hand state to downstream transforms within the same
    /// response. TopHat itself does not inspect the contents. Not thread-safe; transforms run
    /// sequentially.
    /// </summary>
    public IDictionary<string, object?> Properties { get; }
}
