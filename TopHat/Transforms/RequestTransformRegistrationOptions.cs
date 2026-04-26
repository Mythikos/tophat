using TopHat.Providers;

namespace TopHat.Transforms;

/// <summary>
/// Fluent configuration for registering a request transform. Passed into the
/// <c>AddTopHatRequestTransform&lt;T&gt;</c> callback.
/// </summary>
public sealed class RequestTransformRegistrationOptions
{
    /// <summary>Ascending sort key. Ties broken by registration order (stable).</summary>
    public int Order { get; private set; }

    /// <summary>How the pipeline handles exceptions thrown by this transform.</summary>
    public TransformFailureMode FailureMode { get; private set; } = TransformFailureMode.FailOpen;

    internal Func<RequestTransformContext, bool>? Filter { get; private set; }

    /// <summary>Filter to targets. No-op if the array is empty.</summary>
    public RequestTransformRegistrationOptions AppliesTo(params TopHatTarget[] targets)
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
    public RequestTransformRegistrationOptions AppliesTo(Func<RequestTransformContext, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        this.Filter = predicate;
        return this;
    }

    /// <summary>Sets the sort order.</summary>
    public RequestTransformRegistrationOptions WithOrder(int order)
    {
        this.Order = order;
        return this;
    }

    /// <summary>Sets the failure mode.</summary>
    public RequestTransformRegistrationOptions WithFailureMode(TransformFailureMode mode)
    {
        this.FailureMode = mode;
        return this;
    }
}
