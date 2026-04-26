using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Net.Http.Headers;
using TopHat.Body;
using TopHat.Compression.CCR;
using TopHat.Configuration;
using TopHat.Diagnostics;
using TopHat.Providers;
using TopHat.Streaming;
using TopHat.Transforms;

namespace TopHat.Handlers;

/// <summary>
/// Delegating handler that intercepts outbound HTTP requests, optionally rewrites them to a configured
/// gateway, runs the configured request-transform pipeline, and records metrics + structured logs.
/// Streaming responses flow through without buffering — the handler never reads response bytes directly;
/// observation happens via a <see cref="TeeStream"/> wrapper that forwards to the caller verbatim.
/// </summary>
public sealed class TopHatHandler : DelegatingHandler
{
    /// <summary>
    /// <see cref="HttpRequestOptions"/> key that, when set to <c>true</c>, causes this handler to bypass
    /// its pipeline for the request. Equivalent to the bypass header but never serializes.
    /// </summary>
    public const string BypassOptionsKey = "TopHat.Bypass";

    private static readonly HttpRequestOptionsKey<bool> s_bypassKey = new(BypassOptionsKey);

    private readonly IOptions<TopHatOptions> _options;
    private readonly ILogger<TopHatHandler> _logger;
    private readonly TopHatTargetDetector _detector;
    private readonly RequestBodyInspector _inspector;
    private readonly TransformPipeline? _transformPipeline;
    private readonly ResponseTransformPipeline? _responseTransformPipeline;
    private readonly Dictionary<TopHatTarget, ICCROrchestrator> _ccrOrchestrators;

    /// <summary>
    /// Constructs the handler. Typically resolved from DI via <c>AddHttpMessageHandler&lt;TopHatHandler&gt;()</c>,
    /// but can be constructed manually when not using DI (target classification and body inspection are
    /// pure functions of <see cref="TopHatOptions"/> — no extra services required). Pass a non-null
    /// <paramref name="serviceProvider"/> to enable the transform pipeline; pass null to skip it.
    /// </summary>
    public TopHatHandler(IOptions<TopHatOptions> options, ILogger<TopHatHandler> logger, IServiceProvider? serviceProvider = null)
    {
        this._options = options ?? throw new ArgumentNullException(nameof(options));
        this._logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this._detector = new TopHatTargetDetector(options);
        this._inspector = new RequestBodyInspector(options, logger);

        var orchestratorMap = new Dictionary<TopHatTarget, ICCROrchestrator>();

        if (serviceProvider is not null)
        {
            var registry = (TopHatTransformRegistry?)serviceProvider.GetService(typeof(TopHatTransformRegistry));
            if (registry is not null && registry.Registrations.Count > 0)
            {
                this._transformPipeline = new TransformPipeline(registry, serviceProvider, logger);
                var responsePipeline = new ResponseTransformPipeline(registry, serviceProvider, logger);
                if (responsePipeline.HasAny)
                {
                    this._responseTransformPipeline = responsePipeline;
                }
            }

            var orchestrators = (IEnumerable<ICCROrchestrator>?)serviceProvider.GetService(typeof(IEnumerable<ICCROrchestrator>));
            if (orchestrators is not null)
            {
                foreach (var orchestrator in orchestrators)
                {
                    // Last-registered wins on duplicate targets — matches DI replacement semantics.
                    orchestratorMap[orchestrator.Target] = orchestrator;
                }
            }
        }

        this._ccrOrchestrators = orchestratorMap;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = CreateContext();
        context.CancellationToken = cancellationToken;
        if (this.TryBypass(request, context))
        {
            return await this.SendBypassAsync(request, context, cancellationToken).ConfigureAwait(false);
        }

        this.DetectProvider(request, context);
        this.RewriteUri(request, context);
        this.DetectTarget(request, context);
        await this._inspector.InspectAsync(request, context, cancellationToken).ConfigureAwait(false);

        if (this._transformPipeline is not null)
        {
            await this._transformPipeline.RunAsync(request, context, cancellationToken).ConfigureAwait(false);
        }

        UpdateStreamingTag(request, context);
        ObserveRequest(request, context);

        var response = await this.SendUpstreamAsync(request, context, cancellationToken).ConfigureAwait(false);

        if (this._ccrOrchestrators.Count > 0 && this._ccrOrchestrators.TryGetValue(context.Target, out var ccr))
        {
            // Orchestrator may recurse (fulfilling tophat_retrieve tool_use calls via
            // base.SendAsync for each follow-up hop) and ultimately return a different response.
            // base.SendAsync is passed directly — follow-up hops bypass TopHatHandler's own
            // pre-processing (no re-compressing, no re-injecting tools) and just flow through
            // any remaining inner handlers.
            var ccrContext = new CCROrchestrationContext(
                originalRequest: request,
                initialResponse: response,
                sendUpstream: base.SendAsync,
                localId: context.LocalId,
                logger: this._logger);
            response = await ccr.OrchestrateAsync(ccrContext, cancellationToken).ConfigureAwait(false);
        }

        this.WrapResponse(response, context);
        this.RecordRequestMetrics(request, response, context);
        return response;
    }

    private static TopHatRequestContext CreateContext() => new()
    {
        LocalId = Guid.NewGuid().ToString("N"),
        Stopwatch = Stopwatch.StartNew(),
    };

    private bool TryBypass(HttpRequestMessage request, TopHatRequestContext context)
    {
        if (request.Options.TryGetValue(s_bypassKey, out var flag) && flag)
        {
            context.Bypassed = true;
            context.BypassSource = BypassSource.Options;
            return true;
        }

        var headerName = this._options.Value.BypassHeaderName;
        if (!string.IsNullOrEmpty(headerName) &&
            request.Headers.TryGetValues(headerName, out var values))
        {
            foreach (var value in values)
            {
                if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
                {
                    // Strip so the header does not leak upstream.
                    request.Headers.Remove(headerName);
                    context.Bypassed = true;
                    context.BypassSource = BypassSource.Header;
                    return true;
                }
            }
        }

        return false;
    }

    private async Task<HttpResponseMessage> SendBypassAsync(HttpRequestMessage request, TopHatRequestContext context, CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response;
        }
        finally
        {
            context.Stopwatch.Stop();
            var statusCode = response is null ? 0 : (int)response.StatusCode;
            var tags = new TagList
            {
                { "target", "bypassed" },
                { "method", request.Method.Method },
                { "status_code", statusCode },
                { "bypass", "true" },
            };
            TopHatMetrics.Requests.Add(1, tags);

            if (this._options.Value.LogRequests)
            {
                TopHatLogEvents.RequestBypassed(this._logger, request.Method.Method, request.RequestUri?.Host, request.RequestUri?.AbsolutePath, context.BypassSource, statusCode, context.Stopwatch.Elapsed.TotalMilliseconds, context.LocalId);
            }
        }
    }

    private void DetectProvider(HttpRequestMessage request, TopHatRequestContext context)
    {
        context.Provider = this._detector.DetectProvider(request);
    }

    private void RewriteUri(HttpRequestMessage request, TopHatRequestContext context)
    {
        var opts = this._options.Value;
        var baseUrl = context.Provider switch
        {
            TopHatProviderKind.Anthropic => opts.AnthropicBaseUrl,
            TopHatProviderKind.OpenAI => opts.OpenAiBaseUrl,
            _ => null,
        };

        if (baseUrl is null || request.RequestUri is null)
        {
            return;
        }

        var original = request.RequestUri;
        var builder = new UriBuilder(original)
        {
            Scheme = baseUrl.Scheme,
            Host = baseUrl.Host,
            Port = baseUrl.IsDefaultPort ? -1 : baseUrl.Port,
        };

        request.RequestUri = builder.Uri;
        context.UriRewritten = true;

        TopHatLogEvents.UriRewritten(this._logger, original.Host, builder.Host, context.LocalId);
    }

    private void DetectTarget(HttpRequestMessage request, TopHatRequestContext context)
    {
        context.Target = this._detector.DetectTarget(request);

        if (context.Target == TopHatTarget.Unknown && context.Provider != TopHatProviderKind.Other)
        {
            TopHatLogEvents.UnknownTarget(this._logger, request.Method.Method, request.RequestUri?.Host, request.RequestUri?.AbsolutePath, context.LocalId);
        }
    }

    private static void UpdateStreamingTag(HttpRequestMessage request, TopHatRequestContext context)
    {
        // Body-authoritative when inspection succeeded; falls back to Accept header otherwise.
        context.Streaming = context.StreamingFromBody || HasSseAccept(request);
    }

    private static void ObserveRequest(HttpRequestMessage request, TopHatRequestContext context)
    {
        // Runs AFTER transforms so RequestBytes reflects the post-transform wire size.
        context.RequestBytes = request.Content?.Headers.ContentLength;
    }

    private async Task<HttpResponseMessage> SendUpstreamAsync(HttpRequestMessage request, TopHatRequestContext context, CancellationToken cancellationToken)
    {
        try
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or TimeoutException)
        {
            var elapsed = context.Stopwatch.Elapsed;
            context.Stopwatch.Stop();
            var kind = ClassifyError(ex, cancellationToken);
            var errorTags = new TagList
            {
                { "target", context.Target.ToString() },
                { "kind", kind },
            };
            TopHatMetrics.UpstreamErrors.Add(1, errorTags);

            TopHatLogEvents.UpstreamError(this._logger, ex, kind, request.Method.Method, request.RequestUri?.Host, request.RequestUri?.AbsolutePath, elapsed.TotalMilliseconds, context.LocalId, context.UpstreamRequestId);

            throw;
        }
    }

    private void WrapResponse(HttpResponseMessage response, TopHatRequestContext context)
    {
        var innerContent = response.Content;
        if (innerContent is null)
        {
            return;
        }

        var recorder = context.Provider switch
        {
            TopHatProviderKind.Anthropic => new UsageRecorder(context.Provider, context.Target.ToString(), context.Model),
            TopHatProviderKind.OpenAI => new UsageRecorder(context.Provider, context.Target.ToString(), context.Model),
            _ => (UsageRecorder?)null,
        };

        Func<TeeStream, CancellationToken, ValueTask>? asyncCallback = null;
        Action<TeeStream>? syncCallback = null;

        if (this._responseTransformPipeline is not null)
        {
            var responseHeaders = response.Headers;
            var statusCode = (int)response.StatusCode;
            asyncCallback = (tee, ct) => this.DispatchResponseTransformsAsync(tee, responseHeaders, statusCode, context, ct);
            syncCallback = tee => TopHatLogEvents.ResponseTransformsSkippedSyncDispose(this._logger, context.LocalId);
        }

        response.Content = new ObservingHttpContent(innerContent, context, (int)response.StatusCode, recorder, this._options, this._logger, asyncCallback, syncCallback);
    }

    private async ValueTask DispatchResponseTransformsAsync(TeeStream tee, HttpResponseHeaders responseHeaders, int statusCode, TopHatRequestContext context, CancellationToken cancellationToken)
    {
        if (this._responseTransformPipeline is null)
        {
            return;
        }

        var observedEvents = tee.Mode == TeeMode.Sse
            ? tee.Observations
            : null;

        var effectiveToken = cancellationToken == CancellationToken.None ? context.CancellationToken : cancellationToken;

        var responseContext = new ResponseTransformContext(
            provider: context.Provider,
            target: context.Target,
            model: context.Model,
            localId: context.LocalId,
            statusCode: statusCode,
            headers: responseHeaders,
            mode: tee.Mode,
            body: tee.Mode == TeeMode.WholeBody ? tee.WholeBody : null,
            observedEvents: observedEvents,
            observedEventCount: tee.FrameCount,
            truncatedObservedEvents: tee.ObservationsTruncated,
            logger: this._logger,
            properties: new Dictionary<string, object?>(StringComparer.Ordinal),
            cancellationToken: effectiveToken);

        await this._responseTransformPipeline.RunAsync(responseContext, effectiveToken).ConfigureAwait(false);
    }

    private void RecordRequestMetrics(HttpRequestMessage request, HttpResponseMessage response, TopHatRequestContext context)
    {
        // Snapshot TTFB without stopping the watch; the tee uses the same watch for total duration.
        context.TtfbElapsed = context.Stopwatch.Elapsed;

        if (response.Headers.TryGetValues("request-id", out var ids))
        {
            context.UpstreamRequestId = ids.FirstOrDefault();
        }

        var statusCode = (int)response.StatusCode;
        var targetTag = context.Target.ToString();
        var ttfbMs = context.TtfbElapsed.TotalMilliseconds;

        var requestTags = new TagList
        {
            { "target", targetTag },
            { "method", request.Method.Method },
            { "status_code", statusCode },
            { "streaming", context.Streaming ? "true" : "false" },
            { "bypass", "false" },
            { "model", context.Model },
        };
        TopHatMetrics.Requests.Add(1, requestTags);

        var ttfbTags = new TagList
        {
            { "target", targetTag },
            { "status_code", statusCode },
        };
        TopHatMetrics.RequestTtfb.Record(ttfbMs, ttfbTags);

        if (context.RequestBytes is long reqBytes)
        {
            var rbTags = new TagList { { "target", targetTag } };
            TopHatMetrics.RequestBytes.Record(reqBytes, rbTags);
        }

        if (response.Content?.Headers.ContentLength is long respBytes)
        {
            var rsTags = new TagList
            {
                { "target", targetTag },
                { "status_code", statusCode },
            };
            TopHatMetrics.ResponseBytes.Record(respBytes, rsTags);
        }

        if (this._options.Value.LogRequests)
        {
            TopHatLogEvents.RequestForwarded(this._logger, request.Method.Method, request.RequestUri?.Host, request.RequestUri?.AbsolutePath, statusCode, ttfbMs, context.Target, context.Streaming, context.LocalId, context.UpstreamRequestId);
        }
    }

    private static bool HasSseAccept(HttpRequestMessage request)
    {
        foreach (var accept in request.Headers.Accept)
        {
            if (string.Equals(accept.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string ClassifyError(Exception ex, CancellationToken cancellationToken)
    {
        if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return "canceled";
        }

        if (ex is TaskCanceledException)
        {
            // TaskCanceledException without user cancellation is HttpClient's timeout signal.
            return "timeout";
        }

        if (ex is TimeoutException)
        {
            return "timeout";
        }

        if (ex is HttpRequestException httpEx)
        {
            return httpEx.InnerException switch
            {
                System.Net.Sockets.SocketException => "connection",
                IOException => "connection",
                _ => "other",
            };
        }

        return "other";
    }
}
