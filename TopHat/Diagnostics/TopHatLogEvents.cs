using Microsoft.Extensions.Logging;
using TopHat.Handlers;
using TopHat.Providers;

namespace TopHat.Diagnostics;

/// <summary>
/// High-performance structured log emitters backed by the [LoggerMessage] source generator.
/// Event IDs are stable public contract for log-routing pipelines.
/// </summary>
internal static partial class TopHatLogEvents
{
    public const int RequestForwardedId = 1000;
    public const int RequestBypassedId = 1001;
    public const int UriRewrittenId = 1002;
    public const int UpstreamErrorId = 1003;
    public const int UnknownTargetId = 1004;
    public const int BodyInspectionSkippedId = 1005;
    public const int ParserErrorId = 1006;
    public const int TransformErrorId = 1007;
    public const int TransformSkippedId = 1008;
    public const int CacheAlignerAppliedId = 1009;
    public const int CacheAlignerSkippedId = 1010;
    public const int PromptStabilizerAppliedId = 1011;
    public const int PromptStabilizerSkippedId = 1012;
    public const int ResponseTransformInvokedId = 1013;
    public const int ResponseTransformSkippedId = 1014;
    public const int ResponseTransformFailedId = 1015;
    public const int ResponseTransformsSkippedSyncDisposeId = 1016;
    public const int JsonContextCompressorAppliedId = 1017;
    public const int JsonContextCompressorSkippedId = 1018;

    [LoggerMessage(EventId = RequestForwardedId, Level = LogLevel.Information, Message = "TopHat forwarded {Method} {Host}{Path} -> {StatusCode} in {TtfbMs}ms (Target={Target}, Streaming={Streaming}, LocalId={LocalId}, UpstreamRequestId={UpstreamRequestId})")]
    public static partial void RequestForwarded(ILogger logger, string? method, string? host, string? path, int statusCode, double ttfbMs, TopHatTarget target, bool streaming, string localId, string? upstreamRequestId);

    [LoggerMessage(EventId = RequestBypassedId, Level = LogLevel.Information, Message = "TopHat bypassed {Method} {Host}{Path} via {BypassSource}; status {StatusCode} in {TtfbMs}ms (LocalId={LocalId})")]
    public static partial void RequestBypassed(ILogger logger, string? method, string? host, string? path, BypassSource bypassSource, int statusCode, double ttfbMs, string localId);

    [LoggerMessage(EventId = UriRewrittenId, Level = LogLevel.Debug, Message = "TopHat rewrote {OriginalHost} -> {RewrittenHost} (path+query preserved) (LocalId={LocalId})")]
    public static partial void UriRewritten(ILogger logger, string originalHost, string rewrittenHost, string localId);

    [LoggerMessage(EventId = UpstreamErrorId, Level = LogLevel.Warning, Message = "TopHat upstream error {Kind} for {Method} {Host}{Path} after {TtfbMs}ms (LocalId={LocalId}, UpstreamRequestId={UpstreamRequestId})")]
    public static partial void UpstreamError(ILogger logger, Exception exception, string kind, string? method, string? host, string? path, double ttfbMs, string localId, string? upstreamRequestId);

    [LoggerMessage(EventId = UnknownTargetId, Level = LogLevel.Debug, Message = "TopHat saw unknown target on known provider host: {Method} {Host}{Path} (LocalId={LocalId})")]
    public static partial void UnknownTarget(ILogger logger, string? method, string? host, string? path, string localId);

    [LoggerMessage(EventId = BodyInspectionSkippedId, Level = LogLevel.Debug, Message = "TopHat skipped request-body inspection: reason={Reason} (LocalId={LocalId})")]
    public static partial void BodyInspectionSkipped(ILogger logger, string reason, string localId);

    [LoggerMessage(EventId = ParserErrorId, Level = LogLevel.Debug, Message = "TopHat response parser error: kind={Kind} (LocalId={LocalId}, Target={Target})")]
    public static partial void ParserError(ILogger logger, string kind, string localId, TopHatTarget target);

    [LoggerMessage(EventId = TransformErrorId, Level = LogLevel.Warning, Message = "TopHat transform {TransformName} threw {Kind} (FailureMode={FailureMode}, Target={Target}, LocalId={LocalId})")]
    public static partial void TransformError(ILogger logger, Exception exception, string transformName, string kind, string failureMode, TopHatTarget target, string localId);

    [LoggerMessage(EventId = TransformSkippedId, Level = LogLevel.Debug, Message = "TopHat transform {TransformName} skipped by filter (Target={Target}, LocalId={LocalId})")]
    public static partial void TransformSkipped(ILogger logger, string transformName, TopHatTarget target, string localId);

    [LoggerMessage(EventId = CacheAlignerAppliedId, Level = LogLevel.Debug, Message = "TopHat cache aligner applied breakpoints {Breakpoints} on {Model} (prefix_chars={PrefixChars}, LocalId={LocalId})")]
    public static partial void CacheAlignerApplied(ILogger logger, string breakpoints, string model, int prefixChars, string localId);

    [LoggerMessage(EventId = CacheAlignerSkippedId, Level = LogLevel.Debug, Message = "TopHat cache aligner skipped: reason={Reason} (Model={Model}, LocalId={LocalId})")]
    public static partial void CacheAlignerSkipped(ILogger logger, string reason, string model, string localId);

    [LoggerMessage(EventId = PromptStabilizerAppliedId, Level = LogLevel.Debug, Message = "TopHat prompt stabilizer applied to {Shape} on {Model} (moved_spans={MovedSpans}, LocalId={LocalId})")]
    public static partial void PromptStabilizerApplied(ILogger logger, string shape, string model, int movedSpans, string localId);

    [LoggerMessage(EventId = PromptStabilizerSkippedId, Level = LogLevel.Debug, Message = "TopHat prompt stabilizer skipped: reason={Reason} (Model={Model}, LocalId={LocalId})")]
    public static partial void PromptStabilizerSkipped(ILogger logger, string reason, string model, string localId);

    [LoggerMessage(EventId = ResponseTransformInvokedId, Level = LogLevel.Debug, Message = "TopHat response transform {TransformName} invoked (Target={Target}, LocalId={LocalId})")]
    public static partial void ResponseTransformInvoked(ILogger logger, string transformName, TopHatTarget target, string localId);

    [LoggerMessage(EventId = ResponseTransformSkippedId, Level = LogLevel.Debug, Message = "TopHat response transform {TransformName} skipped by filter (Target={Target}, LocalId={LocalId})")]
    public static partial void ResponseTransformSkipped(ILogger logger, string transformName, TopHatTarget target, string localId);

    [LoggerMessage(EventId = ResponseTransformFailedId, Level = LogLevel.Warning, Message = "TopHat response transform {TransformName} threw {Kind} (FailureMode={FailureMode}, Target={Target}, LocalId={LocalId})")]
    public static partial void ResponseTransformFailed(ILogger logger, Exception exception, string transformName, string kind, string failureMode, TopHatTarget target, string localId);

    [LoggerMessage(EventId = ResponseTransformFailedId, Level = LogLevel.Warning, Message = "TopHat response transform {TransformName} filter threw (Target={Target}, LocalId={LocalId})")]
    public static partial void ResponseTransformFilterError(ILogger logger, Exception exception, string transformName, TopHatTarget target, string localId);

    [LoggerMessage(EventId = ResponseTransformsSkippedSyncDisposeId, Level = LogLevel.Debug, Message = "TopHat response-transform pipeline skipped: response stream was synchronously disposed. Callers who register response transforms must use async disposal (await using / ReadAsStreamAsync / ReadAsByteArrayAsync). (LocalId={LocalId})")]
    public static partial void ResponseTransformsSkippedSyncDispose(ILogger logger, string localId);

    [LoggerMessage(EventId = JsonContextCompressorAppliedId, Level = LogLevel.Debug, Message = "TopHat JSON context compressor applied: compressed={Compressed} skipped={Skipped} (Target={Target}, LocalId={LocalId})")]
    public static partial void JsonContextCompressorApplied(ILogger logger, int compressed, int skipped, Providers.TopHatTarget target, string localId);

    [LoggerMessage(EventId = JsonContextCompressorSkippedId, Level = LogLevel.Debug, Message = "TopHat JSON context compressor skipped: reason={Reason} (Target={Target}, LocalId={LocalId})")]
    public static partial void JsonContextCompressorSkipped(ILogger logger, string reason, Providers.TopHatTarget target, string localId);
}
