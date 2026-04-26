using System.Diagnostics.Metrics;

namespace TopHat.Diagnostics;

/// <summary>
/// Single point of access to TopHat's <see cref="System.Diagnostics.Metrics.Meter"/> and the instruments it owns.
/// Consumers wire metrics into OpenTelemetry with <c>.AddMeter(TopHatMetrics.MeterName)</c>.
/// </summary>
/// <remarks>
/// The <see cref="Meter"/> itself is process-lifetime and must never be disposed by this library.
/// </remarks>
public static class TopHatMetrics
{
    /// <summary>Name of the <see cref="System.Diagnostics.Metrics.Meter"/> TopHat emits on.</summary>
    public const string MeterName = "TopHat";

    private static readonly string? s_assemblyVersion = typeof(TopHatMetrics).Assembly.GetName().Version?.ToString();

    internal static readonly Meter Meter = new(MeterName, s_assemblyVersion);

    /// <summary>
    /// Counter incremented for every request TopHat intercepts (including bypassed requests).
    /// Tags: target, method, status_code (raw), streaming, bypass, model.
    /// </summary>
    /// <remarks>
    /// <para><c>status_code</c> is raw (e.g. 200, 429, 529, 520) — upstream-specific codes carry
    /// debugging signal. OpenTelemetry consumers can bucket at export time if cardinality matters.</para>
    /// <para><c>model</c> is the raw upstream string (or the sentinel <c>unknown</c> when body
    /// inspection was skipped). Documented cardinality source — bucket at export if needed.</para>
    /// </remarks>
    internal static readonly Counter<long> Requests = Meter.CreateCounter<long>("tophat.requests", unit: "{request}", description: "Requests intercepted by TopHat.");

    /// <summary>
    /// Histogram of time from <c>SendAsync</c> entry to the point response headers are received (TTFB).
    /// For streaming responses this is NOT total duration; full-stream duration is <see cref="RequestDuration"/>.
    /// </summary>
    internal static readonly Histogram<double> RequestTtfb = Meter.CreateHistogram<double>("tophat.request.ttfb", unit: "ms", description: "Time to first byte (response headers received).");

    /// <summary>
    /// Histogram of request Content-Length when known. Measured AFTER request transforms run.
    /// </summary>
    internal static readonly Histogram<long> RequestBytes = Meter.CreateHistogram<long>("tophat.request.bytes", unit: "By", description: "Request Content-Length (post-transform) when known.");

    /// <summary>
    /// Histogram of response Content-Length when known. Streaming responses often omit Content-Length.
    /// </summary>
    internal static readonly Histogram<long> ResponseBytes = Meter.CreateHistogram<long>("tophat.response.bytes", unit: "By", description: "Response Content-Length when known.");

    /// <summary>
    /// Counter of upstream errors. Tag <c>kind</c>: timeout | connection | canceled | other.
    /// </summary>
    internal static readonly Counter<long> UpstreamErrors = Meter.CreateCounter<long>("tophat.upstream.errors", unit: "{error}", description: "Upstream errors classified by kind.");

    /// <summary>
    /// Histogram of total request duration (stream close). See <see cref="RequestTtfb"/> for time-to-headers.
    /// </summary>
    internal static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>("tophat.request.duration", unit: "ms", description: "Total request duration (stream close).");

    /// <summary>
    /// Counter of errors encountered by the SSE/JSON parsers. Never propagates to the caller.
    /// Tag <c>kind</c>: event_parse | framing | internal.
    /// </summary>
    internal static readonly Counter<long> ParserErrors = Meter.CreateCounter<long>("tophat.parser.errors", unit: "{error}", description: "Response-observer parser errors, classified by kind.");

    /// <summary>
    /// Counter of response-stream outcomes. Tag <c>outcome</c>: eof | disposed | error.
    /// </summary>
    internal static readonly Counter<long> StreamOutcome = Meter.CreateCounter<long>("tophat.stream.outcome", unit: "{stream}", description: "Response stream close outcomes.");

    /// <summary>
    /// Input tokens counter. Tags: target, model.
    /// </summary>
    internal static readonly Counter<long> TokensInput = Meter.CreateCounter<long>("tophat.tokens.input", unit: "{token}", description: "Input tokens reported by upstream.");

    /// <summary>
    /// Output tokens counter. Tags: target, model.
    /// </summary>
    internal static readonly Counter<long> TokensOutput = Meter.CreateCounter<long>("tophat.tokens.output", unit: "{token}", description: "Output tokens reported by upstream.");

    /// <summary>
    /// Cache-read tokens counter. Key signal for validating cache-alignment transforms.
    /// </summary>
    internal static readonly Counter<long> TokensCacheRead = Meter.CreateCounter<long>("tophat.tokens.cache_read", unit: "{token}", description: "Cache-read tokens reported by upstream.");

    /// <summary>
    /// Cache-creation tokens counter.
    /// </summary>
    internal static readonly Counter<long> TokensCacheCreation = Meter.CreateCounter<long>("tophat.tokens.cache_creation", unit: "{token}", description: "Cache-creation tokens reported by upstream.");

    /// <summary>
    /// Counter incremented every time a request transform runs (post-filter). Tags: target, transform_name.
    /// </summary>
    internal static readonly Counter<long> TransformInvoked = Meter.CreateCounter<long>("tophat.transform.invoked", unit: "{invoke}", description: "Transform invocations (post-filter).");

    /// <summary>
    /// Counter incremented when a transform calls <c>MarkMutated()</c> on its context. Tags: target, transform_name.
    /// </summary>
    internal static readonly Counter<long> TransformMutated = Meter.CreateCounter<long>("tophat.transform.mutated", unit: "{mutation}", description: "Transform invocations that reported a mutation.");

    /// <summary>
    /// Counter of transform exceptions. Tags: target, transform_name, kind (exception type), failure_mode.
    /// </summary>
    internal static readonly Counter<long> TransformErrors = Meter.CreateCounter<long>("tophat.transform.errors", unit: "{error}", description: "Transform exceptions, classified by kind and failure mode.");

    /// <summary>
    /// Counter of transforms that decided not to run on a given request. Tags: target, transform_name,
    /// reason. Cardinality bounded by the enum-like set of reasons: unsupported_model, below_threshold,
    /// already_optimized, no_system_or_tools, system_restructure_disallowed, filter, regex_timeout.
    /// </summary>
    internal static readonly Counter<long> TransformSkipped = Meter.CreateCounter<long>("tophat.transform.skipped", unit: "{skip}", description: "Transform skip decisions, classified by reason.");

    /// <summary>
    /// Counter incremented every time a CCR orchestrator runs to completion on a request.
    /// Tags: target (Anthropic/OpenAI surface), outcome (single_hop, multi_hop, foreign_tool_use,
    /// budget_exhausted, not_orchestratable, parse_failure). Lets dashboards distinguish "CCR
    /// fired and helped" from "CCR fired and chewed budget" from "model didn't engage."
    /// </summary>
    internal static readonly Counter<long> CCROrchestrations = Meter.CreateCounter<long>("tophat.ccr.orchestrations", unit: "{orchestration}", description: "CCR orchestrator invocations, classified by outcome.");

    /// <summary>
    /// Histogram of upstream hop count per CCR-orchestrated request (initial dispatch counts as
    /// hop 1). Tag: target. The single-hop case is the dominant bucket — multi-hop tail is the
    /// signal worth watching, since it's where CCR overhead lives.
    /// </summary>
    internal static readonly Histogram<int> CCRHops = Meter.CreateHistogram<int>("tophat.ccr.hops", unit: "{hop}", description: "Upstream hop count per CCR-orchestrated request (initial dispatch counts as hop 1).");

    /// <summary>
    /// Tokens in the request body BEFORE TopHat transforms ran. Counted by whichever
    /// <see cref="TopHat.Tokenizers.ITokenizer"/> is registered for the request's target —
    /// chars/4 fallback when no provider-specific tokenizer is installed, bit-exact when
    /// <c>TopHat.Tokenizers.OpenAi</c> or <c>TopHat.Tokenizers.Anthropic</c> is wired.
    /// Tags: target, model, tokenizer_kind. The <c>tokenizer_kind</c> tag identifies the
    /// data source so dashboards can filter approximations or compare drift.
    /// </summary>
    internal static readonly Counter<long> RequestTokensPreTransform = Meter.CreateCounter<long>("tophat.request.tokens.pre_transform", unit: "{token}", description: "Tokens of request body before TopHat transforms ran (counted by registered ITokenizer).");

    /// <summary>
    /// Tokens in the request body AFTER TopHat transforms ran. Same tokenizer/tags as
    /// <see cref="RequestTokensPreTransform"/>. Pair them to answer "how much did TopHat
    /// compress this request?" — accurately if a provider-specific tokenizer is installed,
    /// approximately under the chars/4 default.
    /// </summary>
    internal static readonly Counter<long> RequestTokensPostTransform = Meter.CreateCounter<long>("tophat.request.tokens.post_transform", unit: "{token}", description: "Tokens of request body after TopHat transforms ran (counted by registered ITokenizer).");

    /// <summary>
    /// Histogram of per-request payload reduction ratio: <c>(pre_transform - post_transform) / pre_transform</c>.
    /// Range [0, 1] — 0 means transforms changed nothing, 1 would mean everything was elided.
    /// Tags: target. High values + low <see cref="CCROrchestrations"/> multi-hop fire = aggressive
    /// compression that the model isn't recovering from via retrieval, which is an accuracy-risk
    /// signal worth watching.
    /// </summary>
    internal static readonly Histogram<double> CompressionReductionRatio = Meter.CreateHistogram<double>("tophat.compression.payload.reduction.ratio", unit: "{ratio}", description: "Per-request payload reduction (1 - post/pre) by TopHat transforms.");

    /// <summary>
    /// Counter incremented when a transform is detected to have mutated cache-relevant content
    /// in a request. Detection algorithm differs per target: Anthropic uses the location of the
    /// first <c>cache_control</c> marker; OpenAI Chat Completions / Responses use "everything
    /// except the last conversation entry" since their automatic prefix caching has no
    /// request-side markers. Tags: target, transform_name (the offender), model. A non-zero
    /// value means TopHat is destroying its own value proposition.
    /// </summary>
    internal static readonly Counter<long> CacheBustsDetected = Meter.CreateCounter<long>("tophat.cache.busts_detected", unit: "{bust}", description: "Cache busts attributable to a TopHat transform mutating cache-relevant content.");
}
