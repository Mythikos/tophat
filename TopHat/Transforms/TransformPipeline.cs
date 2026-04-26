using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using TopHat.Diagnostics;
using TopHat.Handlers;
using TopHat.Providers;
using TopHat.Tokenizers;

namespace TopHat.Transforms;

/// <summary>
/// Executes the configured request-side transforms for a single request. Handles filter evaluation,
/// invocation ordering, failure-mode enforcement, and end-of-pipeline body re-serialization when
/// any transform has marked the context mutated.
/// </summary>
internal sealed class TransformPipeline
{
    private readonly TopHatTransformRegistry _registry;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger _logger;
    private readonly ITokenizer[] _tokenizers;

    public TransformPipeline(TopHatTransformRegistry registry, IServiceProvider serviceProvider, ILogger logger)
    {
        this._registry = registry;
        this._serviceProvider = serviceProvider;
        this._logger = logger;
        // Snapshot the registered tokenizers up-front. AddTopHat registers the chars/4 fallback;
        // provider-specific packages add their own. SelectTokenizer picks the most-specific match
        // per request — non-default wins over the fallback.
        this._tokenizers = serviceProvider.GetServices<ITokenizer>().ToArray();
    }

    public async Task RunAsync(
        HttpRequestMessage request,
        TopHatRequestContext context,
        CancellationToken cancellationToken)
    {
        if (this._registry.Registrations.Count == 0)
        {
            return;
        }

        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        var filtered = this.FilterAndSort(context, properties);
        context.FilteredTransforms = filtered;
        if (filtered.Count == 0)
        {
            return;
        }

        // Pre-transform measurements. The registered ITokenizer determines accuracy and
        // tokenizer_kind tag. Deferred-mode tokenizers return 0 sync and emit the real count
        // out-of-band via the deferredEmit callback we provide here. Cache-prefix hash is
        // computed from the canonical-formatted JsonBody (not raw request bytes) so
        // whitespace-only differences don't surface as false-positive busts.
        var preBodyBytes = context.RequestBodyBytes;
        var tokenizer = this.SelectTokenizer(context.Target);
        var deferredTokenTags = new TagList
        {
            { "target", context.Target.ToString() },
            { "model", context.Model ?? "unknown" },
            { "tokenizer_kind", tokenizer.Kind },
        };
        Action<int> deferredEmitPre = count => TopHatMetrics.RequestTokensPreTransform.Add(count, deferredTokenTags);
        Action<int> deferredEmitPost = count => TopHatMetrics.RequestTokensPostTransform.Add(count, deferredTokenTags);
        var preTokenCount = preBodyBytes is not null
            ? await tokenizer.CountTokensAsync(preBodyBytes, context.Target, context.Model, deferredEmitPre, cancellationToken).ConfigureAwait(false)
            : 0;
        var lastCachePrefixHash = context.JsonBody is JsonObject preBodyObj
            ? CachePrefixHasher.Hash(preBodyObj, context.Target)
            : null;

        foreach (var registration in filtered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mutated = await this.InvokeOneAsync(registration, request, context, properties, cancellationToken).ConfigureAwait(false);

            // Cache bust detection: only worth checking when (a) a cache prefix existed
            // pre-transform and (b) this transform actually mutated. If the post-transform
            // prefix hash differs from what we last saw, this transform busted the cache.
            // The hasher dispatches on target — Anthropic uses cache_control markers, OpenAI
            // uses "everything except the last conversation entry."
            if (mutated && lastCachePrefixHash is not null && context.JsonBody is JsonObject postBodyObj)
            {
                var postHash = CachePrefixHasher.Hash(postBodyObj, context.Target);

                if (postHash is not null && !string.Equals(postHash, lastCachePrefixHash, StringComparison.Ordinal))
                {
                    var bustTags = new TagList
                    {
                        { "target", context.Target.ToString() },
                        { "transform_name", registration.TransformName },
                        { "model", context.Model ?? "unknown" },
                    };
                    TopHatMetrics.CacheBustsDetected.Add(1, bustTags);
                }

                // Roll forward so the next transform's check is against THIS transform's output.
                lastCachePrefixHash = postHash;
            }
        }

        // Re-serialize mutated body if needed before measuring post-transform size.
        if (context.HasMutated && context.JsonBody is not null)
        {
            SerializeMutatedBody(request, context);
        }

        // Post-transform measurements. If the body wasn't mutated, post == pre and the
        // reduction ratio is 0 — that's accurate, not a bug.
        var postBodyBytes = request.Content is not null && context.HasMutated
            ? await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false)
            : preBodyBytes;
        var postTokenCount = postBodyBytes is not null
            ? await tokenizer.CountTokensAsync(postBodyBytes, context.Target, context.Model, deferredEmitPost, cancellationToken).ConfigureAwait(false)
            : 0;

        // For sync tokenizers these emissions ARE the count. For deferred tokenizers the
        // returned values are 0 here — the real counts arrive later via deferredEmitPre/Post.
        // Both paths converge at "cumulative counter holds the real total."
        TopHatMetrics.RequestTokensPreTransform.Add(preTokenCount, deferredTokenTags);
        TopHatMetrics.RequestTokensPostTransform.Add(postTokenCount, deferredTokenTags);

        if (preTokenCount > 0)
        {
            var ratio = (double)(preTokenCount - postTokenCount) / preTokenCount;
            // Clamp to [0, 1] — negative ratios would mean the body grew (rare but possible
            // for transforms that inject markers / metadata). Clamping keeps the histogram
            // semantically "fraction reduced" rather than mixing in growth.
            ratio = Math.Clamp(ratio, 0.0, 1.0);
            TopHatMetrics.CompressionReductionRatio.Record(ratio, new TagList
            {
                { "target", context.Target.ToString() },
                { "tokenizer_kind", tokenizer.Kind },
            });
        }
    }

    /// <summary>
    /// Selects the most-specific <see cref="ITokenizer"/> for the given target. Non-default
    /// (provider-specific) tokenizers win over the chars/4 fallback. Identifies the fallback
    /// by its <see cref="ITokenizer.Kind"/> string rather than by type so users who register
    /// their own approximate-style tokenizer aren't accidentally treated as fallback.
    /// </summary>
    private ITokenizer SelectTokenizer(TopHatTarget target)
    {
        ITokenizer? fallback = null;

        foreach (var candidate in this._tokenizers)
        {
            if (!candidate.SupportsTarget(target))
            {
                continue;
            }

            if (string.Equals(candidate.Kind, "chars_per_token", StringComparison.Ordinal))
            {
                fallback = candidate;
                continue;
            }

            return candidate;
        }

        // Default fallback always supports all targets, so this is only null when nothing
        // is registered at all — defensive throw rather than a silent zero-emission path.
        return fallback ?? throw new InvalidOperationException(
            "No ITokenizer is registered. AddTopHat() should have registered the chars/4 default; " +
            "verify your DI setup includes services.AddTopHat() before resolving TopHatHandler.");
    }

    private List<TransformRegistration> FilterAndSort(
        TopHatRequestContext context,
        IDictionary<string, object?> properties)
    {
        var passed = new List<TransformRegistration>();

        foreach (var registration in this._registry.Registrations)
        {
            if (registration.Kind != TransformKind.Request && registration.Kind != TransformKind.RawRequest)
            {
                continue;
            }

            if (registration.Kind == TransformKind.Request && registration.RequestFilter is not null)
            {
                // Build a preview context (without MarkMutated — filter must be side-effect-free).
                var filterContext = new RequestTransformContext(
                    context.Provider,
                    context.Target,
                    context.Model,
                    context.StreamingFromBody,
                    context.LocalId,
                    context.JsonBody,
                    this._logger,
                    properties,
                    () => { });

                bool included;
                try
                {
                    included = registration.RequestFilter(filterContext);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Filter threw. Conservative: treat as false; log + error-counter with kind=filter.
                    var filterErrorTags = new TagList
                    {
                        { "target", context.Target.ToString() },
                        { "transform_name", registration.TransformName },
                        { "kind", "filter" },
                        { "failure_mode", registration.FailureMode.ToString() },
                        { "phase", "request" },
                    };
                    TopHatMetrics.TransformErrors.Add(1, filterErrorTags);

                    TopHatLogEvents.TransformError(this._logger, ex, registration.TransformName, "filter", registration.FailureMode.ToString(), context.Target, context.LocalId);
                    continue;
                }

                if (!included)
                {
                    TopHatLogEvents.TransformSkipped(this._logger, registration.TransformName, context.Target, context.LocalId);
                    continue;
                }
            }

            passed.Add(registration);
        }

        passed.Sort((a, b) =>
        {
            var orderCmp = a.Order.CompareTo(b.Order);
            return orderCmp != 0 ? orderCmp : a.RegistrationIndex.CompareTo(b.RegistrationIndex);
        });

        return passed;
    }

    private async Task<bool> InvokeOneAsync(
        TransformRegistration registration,
        HttpRequestMessage request,
        TopHatRequestContext context,
        IDictionary<string, object?> properties,
        CancellationToken cancellationToken)
    {
        var invokedTags = new TagList
        {
            { "target", context.Target.ToString() },
            { "transform_name", registration.TransformName },
            { "phase", "request" },
        };
        TopHatMetrics.TransformInvoked.Add(1, invokedTags);

        var mutated = false;
        Action markMutated = () =>
        {
            if (mutated)
            {
                return;
            }

            mutated = true;
            context.HasMutated = true;
            TopHatMetrics.TransformMutated.Add(1, invokedTags);
        };

        using (this._logger.BeginScope(new Dictionary<string, object?>
        {
            ["LocalId"] = context.LocalId,
            ["TransformName"] = registration.TransformName,
            ["UpstreamRequestId"] = context.UpstreamRequestId,
        }))
        {
            try
            {
                switch (registration.Kind)
                {
                    case TransformKind.Request:
                        await this.InvokeRequestAsync(registration, context, properties, markMutated, cancellationToken).ConfigureAwait(false);
                        break;
                    case TransformKind.RawRequest:
                        await this.InvokeRawAsync(registration, request, context, properties, markMutated, cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                this.HandleTransformException(ex, registration, context);
            }
        }

        return mutated;
    }

    private async ValueTask InvokeRequestAsync(
        TransformRegistration registration,
        TopHatRequestContext context,
        IDictionary<string, object?> properties,
        Action markMutated,
        CancellationToken cancellationToken)
    {
        var transform = (IRequestTransform)this._serviceProvider.GetRequiredService(registration.TransformType);
        var transformContext = new RequestTransformContext(
            context.Provider,
            context.Target,
            context.Model,
            context.StreamingFromBody,
            context.LocalId,
            context.JsonBody,
            this._logger,
            properties,
            markMutated);
        await transform.InvokeAsync(transformContext, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask InvokeRawAsync(
        TransformRegistration registration,
        HttpRequestMessage request,
        TopHatRequestContext context,
        IDictionary<string, object?> properties,
        Action markMutated,
        CancellationToken cancellationToken)
    {
        var transform = (IRawRequestTransform)this._serviceProvider.GetRequiredService(registration.TransformType);
        var rawContext = new RawRequestTransformContext(
            request,
            context.Provider,
            context.Target,
            context.Model,
            context.StreamingFromBody,
            context.LocalId,
            this._logger,
            properties,
            markMutated);
        await transform.InvokeAsync(rawContext, cancellationToken).ConfigureAwait(false);
    }

    private void HandleTransformException(Exception ex, TransformRegistration registration, TopHatRequestContext context)
    {
        var kind = ex.GetType().Name;
        var errorTags = new TagList
        {
            { "target", context.Target.ToString() },
            { "transform_name", registration.TransformName },
            { "kind", kind },
            { "failure_mode", registration.FailureMode.ToString() },
            { "phase", "request" },
        };
        TopHatMetrics.TransformErrors.Add(1, errorTags);

        TopHatLogEvents.TransformError(
            this._logger,
            ex,
            registration.TransformName,
            kind,
            registration.FailureMode.ToString(),
            context.Target,
            context.LocalId);

        switch (registration.FailureMode)
        {
            case TransformFailureMode.FailOpen:
                // Restore a clean body from the original snapshot so the next transform sees pristine
                // state. If RequestBodyBytes is null (inspection skipped), there's nothing to restore.
                if (context.RequestBodyBytes is not null)
                {
                    try
                    {
                        context.JsonBody = JsonNode.Parse(context.RequestBodyBytes);
                    }
                    catch (JsonException)
                    {
                        // Original bytes were accepted by the inspector, but we failed to re-parse.
                        // Leave JsonBody as-is; subsequent transforms will see whatever state the
                        // failing transform left.
                    }
                }

                return;

            case TransformFailureMode.FailClosed:
                throw new HttpRequestException(
                    $"TopHat transform '{registration.TransformName}' failed with {kind}; FailureMode=FailClosed blocks upstream send.",
                    ex);

            case TransformFailureMode.CircuitBreaker:
                throw new NotImplementedException(
                    $"TransformFailureMode.CircuitBreaker is not implemented in M3. Transform '{registration.TransformName}' cannot use it.");

            default:
                throw new InvalidOperationException($"Unknown TransformFailureMode: {registration.FailureMode}");
        }
    }

    private static void SerializeMutatedBody(HttpRequestMessage request, TopHatRequestContext context)
    {
        if (context.JsonBody is null)
        {
            return;
        }

        var originalContentType = request.Content?.Headers.ContentType;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(context.JsonBody);

        var replacement = new ByteArrayContent(bytes);
        replacement.Headers.ContentType = originalContentType ?? new MediaTypeHeaderValue("application/json");

        request.Content?.Dispose();
        request.Content = replacement;
    }
}
