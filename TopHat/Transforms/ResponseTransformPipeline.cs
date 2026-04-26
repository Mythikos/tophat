using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using TopHat.Diagnostics;

namespace TopHat.Transforms;

/// <summary>
/// Executes the configured response-side transforms for a single response. Filter-and-sort once,
/// ordered invocation with per-transform logging scope, fail-open by default. Observation-only —
/// there is no mutation or body re-serialization path.
/// </summary>
internal sealed class ResponseTransformPipeline
{
    private const string PhaseTag = "response";

    private readonly TopHatTransformRegistry _registry;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger _logger;

    public ResponseTransformPipeline(TopHatTransformRegistry registry, IServiceProvider serviceProvider, ILogger logger)
    {
        this._registry = registry;
        this._serviceProvider = serviceProvider;
        this._logger = logger;
    }

    /// <summary>Returns true if at least one response-kind registration exists.</summary>
    public bool HasAny
    {
        get
        {
            foreach (var r in this._registry.Registrations)
            {
                if (r.Kind == TransformKind.Response)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public async Task RunAsync(ResponseTransformContext context, CancellationToken cancellationToken)
    {
        var filtered = this.FilterAndSort(context);
        if (filtered.Count == 0)
        {
            return;
        }

        foreach (var registration in filtered)
        {
            // Do NOT throw OperationCanceledException through stream finalization; just stop early.
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await this.InvokeOneAsync(registration, context, cancellationToken).ConfigureAwait(false);
        }
    }

    private List<TransformRegistration> FilterAndSort(ResponseTransformContext context)
    {
        var passed = new List<TransformRegistration>();

        foreach (var registration in this._registry.Registrations)
        {
            if (registration.Kind != TransformKind.Response)
            {
                continue;
            }

            if (registration.ResponseFilter is not null)
            {
                bool included;
                try
                {
                    included = registration.ResponseFilter(context);
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
                        { "phase", PhaseTag },
                    };
                    TopHatMetrics.TransformErrors.Add(1, filterErrorTags);

                    TopHatLogEvents.ResponseTransformFilterError(this._logger, ex, registration.TransformName, context.Target, context.LocalId);
                    continue;
                }

                if (!included)
                {
                    TopHatLogEvents.ResponseTransformSkipped(this._logger, registration.TransformName, context.Target, context.LocalId);
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

    private async Task InvokeOneAsync(TransformRegistration registration, ResponseTransformContext context, CancellationToken cancellationToken)
    {
        var invokedTags = new TagList
        {
            { "target", context.Target.ToString() },
            { "transform_name", registration.TransformName },
            { "phase", PhaseTag },
        };
        TopHatMetrics.TransformInvoked.Add(1, invokedTags);
        TopHatLogEvents.ResponseTransformInvoked(this._logger, registration.TransformName, context.Target, context.LocalId);

        using (this._logger.BeginScope(new Dictionary<string, object?>
        {
            ["LocalId"] = context.LocalId,
            ["TransformName"] = registration.TransformName,
        }))
        {
            try
            {
                var transform = (IResponseTransform)this._serviceProvider.GetRequiredService(registration.TransformType);
                await transform.InvokeAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                this.HandleTransformException(ex, registration, context);
            }
        }
    }

    private void HandleTransformException(Exception ex, TransformRegistration registration, ResponseTransformContext context)
    {
        var kind = ex.GetType().Name;
        var errorTags = new TagList
        {
            { "target", context.Target.ToString() },
            { "transform_name", registration.TransformName },
            { "kind", kind },
            { "failure_mode", registration.FailureMode.ToString() },
            { "phase", PhaseTag },
        };
        TopHatMetrics.TransformErrors.Add(1, errorTags);

        TopHatLogEvents.ResponseTransformFailed(this._logger, ex, registration.TransformName, kind, registration.FailureMode.ToString(), context.Target, context.LocalId);

        switch (registration.FailureMode)
        {
            case TransformFailureMode.FailOpen:
                return;

            case TransformFailureMode.FailClosed:
                throw new HttpRequestException(
                    $"TopHat response transform '{registration.TransformName}' failed with {kind}; FailureMode=FailClosed.",
                    ex);

            case TransformFailureMode.CircuitBreaker:
                throw new NotImplementedException(
                    $"TransformFailureMode.CircuitBreaker is not implemented. Transform '{registration.TransformName}' cannot use it.");

            default:
                throw new InvalidOperationException($"Unknown TransformFailureMode: {registration.FailureMode}");
        }
    }
}
