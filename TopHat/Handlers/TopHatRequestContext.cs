using System.Diagnostics;
using System.Text.Json.Nodes;
using TopHat.Providers;
using TopHat.Streaming;
using TopHat.Transforms;

namespace TopHat.Handlers;

/// <summary>
/// Per-request state threaded through <see cref="TopHatHandler"/>'s pipeline steps.
/// Consumed at log + metric sites so correlation and tag values stay consistent.
/// </summary>
internal sealed class TopHatRequestContext
{
    public required string LocalId { get; init; }

    public required Stopwatch Stopwatch { get; init; }

    public TopHatProviderKind Provider { get; set; } = TopHatProviderKind.Other;

    public TopHatTarget Target { get; set; } = TopHatTarget.Unknown;

    public bool UriRewritten { get; set; }

    public bool Bypassed { get; set; }

    public BypassSource BypassSource { get; set; } = BypassSource.None;

    /// <summary>
    /// Effective streaming flag for metric tagging: <see cref="StreamingFromBody"/> OR
    /// the Accept-header fallback. Set by <c>DetectTarget</c>.
    /// </summary>
    public bool Streaming { get; set; }

    /// <summary>
    /// True iff the request body was inspected and contained <c>stream: true</c>. Primary source
    /// of truth for the streaming tag; falls back to Accept header if body inspection skipped.
    /// </summary>
    public bool StreamingFromBody { get; set; }

    /// <summary>
    /// Model name extracted from the request body, or the sentinel "unknown" when unextractable.
    /// Documented cardinality source — raw upstream value.
    /// </summary>
    public string Model { get; set; } = "unknown";

    public long? RequestBytes { get; set; }

    public string? UpstreamRequestId { get; set; }

    /// <summary>
    /// Parsed request body as a mutable tree. Set by <see cref="Body.RequestBodyInspector"/> on
    /// successful inspection; null otherwise. Transforms mutate this in place.
    /// </summary>
    public JsonNode? JsonBody { get; set; }

    /// <summary>
    /// Snapshot of the original buffered request bytes. Used by the transform pipeline's fail-open
    /// rollback path to re-parse a clean <see cref="JsonBody"/> after a transform throws.
    /// </summary>
    public byte[]? RequestBodyBytes { get; set; }

    /// <summary>
    /// Flipped by <c>RequestTransformContext.MarkMutated</c>. When true at pipeline exit, the
    /// handler serializes <see cref="JsonBody"/> and replaces <c>request.Content</c>.
    /// </summary>
    public bool HasMutated { get; set; }

    /// <summary>
    /// Ordered list of transforms that passed the filter for this request. Materialized once and
    /// cached so log/metric sites can report what actually ran.
    /// </summary>
    public IReadOnlyList<TransformRegistration>? FilteredTransforms { get; set; }

    /// <summary>
    /// Elapsed time at the point response headers were received. Captured by the handler before the
    /// tee starts observing bytes. The stopwatch continues running so the tee can record total
    /// duration at stream close.
    /// </summary>
    public TimeSpan TtfbElapsed { get; set; }

    /// <summary>
    /// How the response stream finalized. Set by <c>TeeStream.FinalizeOnce</c> at stream close.
    /// Remains <see cref="Streaming.StreamOutcome.None"/> for bypassed requests and error cases
    /// that occur before the response is wrapped.
    /// </summary>
    public StreamOutcome Outcome { get; set; } = StreamOutcome.None;

    /// <summary>
    /// Original request's cancellation token captured at <c>SendAsync</c> entry. Surfaced on
    /// <see cref="Transforms.ResponseTransformContext.CancellationToken"/> so response transforms
    /// can honor caller cancellation.
    /// </summary>
    public CancellationToken CancellationToken { get; set; }
}
