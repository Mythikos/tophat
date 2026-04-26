namespace TopHat.Configuration;

/// <summary>
/// Options controlling TopHat's proxy behavior.
/// </summary>
public sealed class TopHatOptions
{
    /// <summary>
    /// When set, requests whose host matches Anthropic (api.anthropic.com) are rewritten to this base URL,
    /// preserving path and query verbatim. When null, Anthropic requests flow through unchanged.
    /// </summary>
    public Uri? AnthropicBaseUrl { get; set; }

    /// <summary>
    /// When set, requests whose host matches OpenAI (api.openai.com) are rewritten to this base URL,
    /// preserving path and query verbatim. When null, OpenAI requests flow through unchanged.
    /// </summary>
    public Uri? OpenAiBaseUrl { get; set; }

    /// <summary>
    /// Header name whose presence with value "true" causes TopHat to bypass its pipeline for that request.
    /// The header is stripped from the outgoing request before forwarding so it does not leak upstream.
    /// </summary>
    public string BypassHeaderName { get; set; } = "x-tophat-bypass";

    /// <summary>
    /// When true, TopHat emits structured log events for intercepted requests. Metrics are always emitted.
    /// </summary>
    public bool LogRequests { get; set; } = true;

    /// <summary>
    /// Maximum size (in bytes) of a JSON request body that TopHat will buffer for inspection.
    /// Inspection extracts <c>stream</c> and <c>model</c> fields for accurate metrics tagging.
    /// Bodies larger than this (or with unknown Content-Length) skip inspection and fall back to
    /// header-based streaming detection with <c>model="unknown"</c>. Default: 10 MB.
    /// </summary>
    public long MaxBodyInspectionBytes { get; set; } = 10L * 1024 * 1024;

    /// <summary>
    /// Maximum size (in bytes) of a non-streaming JSON response body that TopHat will accumulate
    /// in memory to extract usage metrics. Applies only when the response Content-Type is JSON.
    /// SSE responses have their own 64 KB per-frame buffer and ignore this cap. Responses larger
    /// than this cap still forward to the caller unchanged — only usage extraction is abandoned.
    /// Default: 10 MB.
    /// </summary>
    public long MaxResponseInspectionBytes { get; set; } = 10L * 1024 * 1024;
}
