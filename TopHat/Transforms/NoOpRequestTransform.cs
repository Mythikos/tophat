namespace TopHat.Transforms;

/// <summary>
/// Documentation/test baseline. Does nothing. Useful as a copy-from starting point for new transforms,
/// and as a smoke target for the pipeline's invocation + metrics machinery.
/// </summary>
public sealed class NoOpRequestTransform : IRequestTransform
{
    public ValueTask InvokeAsync(RequestTransformContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
