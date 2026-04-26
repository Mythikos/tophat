using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;
using TopHat.Providers;

namespace TopHat.Transforms;

/// <summary>
/// Per-request state exposed to an <see cref="IRequestTransform"/>. Fields are read-only snapshots of
/// the enclosing pipeline state; mutations happen through <see cref="Body"/> (a mutable
/// <see cref="JsonNode"/>) and the opt-in <see cref="MarkMutated"/> signal.
/// </summary>
public sealed class RequestTransformContext
{
    private readonly Action _markMutatedCallback;

    internal RequestTransformContext(TopHatProviderKind provider, TopHatTarget target, string model, bool streamingFromBody, string localId, JsonNode? body, ILogger logger, IDictionary<string, object?> properties, Action markMutatedCallback)
    {
        this.Provider = provider;
        this.Target = target;
        this.Model = model;
        this.StreamingFromBody = streamingFromBody;
        this.LocalId = localId;
        this.Body = body;
        this.Logger = logger;
        this.Properties = properties;
        this._markMutatedCallback = markMutatedCallback;
    }

    /// <summary>Provider classification for the current request.</summary>
    public TopHatProviderKind Provider { get; }

    /// <summary>Target classification for the current request.</summary>
    public TopHatTarget Target { get; }

    /// <summary>Model string extracted from the request body, or <c>"unknown"</c> if inspection was skipped.</summary>
    public string Model { get; }

    /// <summary>
    /// Pre-transform value of the body-authoritative streaming flag. Transforms may mutate the
    /// <c>stream</c> field in <see cref="Body"/>; the final metric tag is derived after all transforms run.
    /// </summary>
    public bool StreamingFromBody { get; }

    /// <summary>Local correlation ID for the request (32-char GUID "N").</summary>
    public string LocalId { get; }

    /// <summary>
    /// Parsed request body, or <c>null</c> if inspection was skipped (non-JSON content, unknown length,
    /// over-cap, parse failure, or null content). Transforms must handle the null case gracefully —
    /// the typical response is to no-op and return.
    /// </summary>
    public JsonNode? Body { get; }

    /// <summary>
    /// Logger scoped to this transform invocation. <c>{LocalId}</c> and <c>{TransformName}</c> are
    /// already pushed to the scope by the pipeline — transforms should not set them manually.
    /// </summary>
    public ILogger Logger { get; }

    /// <summary>
    /// Scratch dictionary for transforms to hand state to downstream transforms within the same request.
    /// TopHat itself does not inspect the contents. Not thread-safe; transforms run sequentially.
    /// </summary>
    public IDictionary<string, object?> Properties { get; }

    /// <summary>
    /// Signals that the transform has mutated <see cref="Body"/> and the pipeline should serialize and
    /// replace <c>HttpRequestMessage.Content</c> at pipeline exit. Idempotent within a single
    /// transform invocation. Forgetting to call this means your mutations will be discarded.
    /// </summary>
    public void MarkMutated() => this._markMutatedCallback();
}
