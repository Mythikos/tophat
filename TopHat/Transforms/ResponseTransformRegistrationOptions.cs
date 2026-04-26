using TopHat.Providers;

namespace TopHat.Transforms;

/// <summary>
/// Fluent configuration for registering an <see cref="IResponseTransform"/>. Passed into the
/// <c>AddTopHatResponseTransform&lt;T&gt;</c> callback.
/// </summary>
/// <remarks>
/// <para>Filter semantic difference from request-side: request-side
/// <c>AppliesTo(Func&lt;RequestTransformContext, bool&gt;)</c> evaluates BEFORE the transform runs,
/// based on pre-request state. Response-side
/// <c>AppliesTo(Func&lt;ResponseTransformContext, bool&gt;)</c> evaluates just-in-time at stream
/// finalization, after <see cref="ResponseTransformContext.StatusCode"/>,
/// <see cref="ResponseTransformContext.Headers"/>, and (for non-streaming)
/// <see cref="ResponseTransformContext.Body"/> are known. Callers relying on status-code gating
/// depend on this timing.</para>
/// <para>If a predicate throws, the pipeline treats the result as <c>false</c> (does NOT invoke),
/// logs at Warning, and increments <c>tophat.transform.errors{phase=response, kind=filter}</c>.</para>
/// </remarks>
public sealed class ResponseTransformRegistrationOptions
{
    /// <summary>Ascending sort key. Ties broken by registration order (stable).</summary>
    public int Order { get; private set; }

    /// <summary>How the pipeline handles exceptions thrown by this transform.</summary>
    public TransformFailureMode FailureMode { get; private set; } = TransformFailureMode.FailOpen;

    internal Func<ResponseTransformContext, bool>? Filter { get; private set; }

    /// <summary>Filter to specific targets. No-op if the array is empty.</summary>
    public ResponseTransformRegistrationOptions AppliesTo(params TopHatTarget[] targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Length == 0)
        {
            return this;
        }

        var set = new HashSet<TopHatTarget>(targets);
        this.Filter = ctx => set.Contains(ctx.Target);
        return this;
    }

    /// <summary>Filter via arbitrary predicate. Overrides any previous <c>AppliesTo</c> call.</summary>
    public ResponseTransformRegistrationOptions AppliesTo(Func<ResponseTransformContext, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        this.Filter = predicate;
        return this;
    }

    /// <summary>Sets the sort order.</summary>
    public ResponseTransformRegistrationOptions WithOrder(int order)
    {
        this.Order = order;
        return this;
    }

    /// <summary>Sets the failure mode.</summary>
    public ResponseTransformRegistrationOptions WithFailureMode(TransformFailureMode mode)
    {
        this.FailureMode = mode;
        return this;
    }
}
