namespace TopHat.Transforms;

/// <summary>
/// Documentation/test baseline. Does nothing. Useful as a copy-from starting point for new response
/// transforms, and as a smoke target for the response pipeline's invocation + metrics machinery.
/// </summary>
public sealed class NoOpResponseTransform : IResponseTransform
{
    public ValueTask InvokeAsync(ResponseTransformContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
