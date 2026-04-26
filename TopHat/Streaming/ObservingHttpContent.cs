using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using TopHat.Configuration;
using TopHat.Handlers;

namespace TopHat.Streaming;

/// <summary>
/// Replacement for <see cref="HttpResponseMessage.Content"/> that wraps the upstream content with
/// a <see cref="TeeStream"/>. Header fidelity is preserved by copying non-auto-computed headers
/// and delegating <see cref="HttpContent.TryComputeLength(out long)"/> to the inner content.
/// </summary>
internal sealed class ObservingHttpContent : HttpContent
{
    private readonly HttpContent _inner;
    private readonly TeeMode _mode;
    private readonly TopHatRequestContext _context;
    private readonly int _statusCode;
    private readonly UsageRecorder? _recorder;
    private readonly IOptions<TopHatOptions> _options;
    private readonly ILogger _logger;
    private readonly Func<TeeStream, CancellationToken, ValueTask>? _asyncFinalizationCallback;
    private readonly Action<TeeStream>? _syncFinalizationCallback;

    public ObservingHttpContent(
        HttpContent inner,
        TopHatRequestContext context,
        int statusCode,
        UsageRecorder? recorder,
        IOptions<TopHatOptions> options,
        ILogger logger,
        Func<TeeStream, CancellationToken, ValueTask>? asyncFinalizationCallback = null,
        Action<TeeStream>? syncFinalizationCallback = null)
    {
        this._inner = inner;
        this._context = context;
        this._statusCode = statusCode;
        this._recorder = recorder;
        this._options = options;
        this._logger = logger;
        this._asyncFinalizationCallback = asyncFinalizationCallback;
        this._syncFinalizationCallback = syncFinalizationCallback;
        this._mode = SelectMode(inner);

        // Copy non-auto-computed headers verbatim. Content-Length is handled via TryComputeLength
        // override; never copy it here (framework will treat it inconsistently).
        foreach (var header in inner.Headers)
        {
            if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            this.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        var innerLength = this._inner.Headers.ContentLength;
        if (innerLength.HasValue)
        {
            length = innerLength.Value;
            return true;
        }

        length = 0;
        return false;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        this.SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        using (var tee = await this.CreateTeeStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            await tee.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        }
    }

    protected override Task<Stream> CreateContentReadStreamAsync() =>
        this.CreateTeeStreamAsync(CancellationToken.None);

    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
        await this.CreateTeeStreamAsync(cancellationToken).ConfigureAwait(false);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this._inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task<Stream> CreateTeeStreamAsync(CancellationToken cancellationToken)
    {
        var innerStream = await this._inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return new TeeStream(
            innerStream,
            this._mode,
            this._context,
            this._statusCode,
            this._recorder,
            this._options,
            this._logger,
            this._asyncFinalizationCallback,
            this._syncFinalizationCallback);
    }

    private static TeeMode SelectMode(HttpContent inner)
    {
        var mediaType = inner.Headers.ContentType?.MediaType;
        if (string.IsNullOrEmpty(mediaType))
        {
            return TeeMode.Passthrough;
        }

        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            return TeeMode.Sse;
        }

        if (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            return TeeMode.WholeBody;
        }

        if (mediaType.StartsWith("application/", StringComparison.OrdinalIgnoreCase) &&
            mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
        {
            return TeeMode.WholeBody;
        }

        return TeeMode.Passthrough;
    }
}
