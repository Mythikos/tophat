namespace TopHat.Transforms;

/// <summary>
/// Runs against a parsed JSON request body before the request is sent upstream. Implementations
/// mutate <see cref="RequestTransformContext.Body"/> in place (a <see cref="System.Text.Json.Nodes.JsonNode"/>)
/// and call <see cref="RequestTransformContext.MarkMutated"/> to signal the pipeline to re-serialize
/// at the end.
/// </summary>
/// <remarks>
/// Transforms are resolved from DI as transient (a fresh instance per request). Cross-request state
/// belongs on the transform class's own fields. Cross-transform state for a single request lives on
/// <see cref="RequestTransformContext.Properties"/>.
/// </remarks>
public interface IRequestTransform
{
    /// <summary>
    /// Invoked once per request, post-filter, in the order configured at registration.
    /// </summary>
    ValueTask InvokeAsync(RequestTransformContext context, CancellationToken cancellationToken);
}
