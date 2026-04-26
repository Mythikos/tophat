using Microsoft.Extensions.Logging;
using TopHat.Providers;

namespace TopHat.Transforms;

/// <summary>
/// Per-request state for an <see cref="IRawRequestTransform"/>. Exposes the full
/// <see cref="HttpRequestMessage"/>; the raw transform owns mutation of headers and content.
/// </summary>
public sealed class RawRequestTransformContext
{
    private readonly Action _markMutatedCallback;

    internal RawRequestTransformContext(HttpRequestMessage request, TopHatProviderKind provider, TopHatTarget target, string model, bool streamingFromBody, string localId, ILogger logger, IDictionary<string, object?> properties, Action markMutatedCallback)
    {
        this.Request = request;
        this.Provider = provider;
        this.Target = target;
        this.Model = model;
        this.StreamingFromBody = streamingFromBody;
        this.LocalId = localId;
        this.Logger = logger;
        this.Properties = properties;
        this._markMutatedCallback = markMutatedCallback;
    }

    public HttpRequestMessage Request { get; }

    public TopHatProviderKind Provider { get; }

    public TopHatTarget Target { get; }

    public string Model { get; }

    public bool StreamingFromBody { get; }

    public string LocalId { get; }

    public ILogger Logger { get; }

    public IDictionary<string, object?> Properties { get; }

    public void MarkMutated() => this._markMutatedCallback();
}
