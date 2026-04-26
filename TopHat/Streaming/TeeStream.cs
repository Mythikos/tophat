using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Buffers;
using System.Diagnostics;
using System.Text.Json.Nodes;
using TopHat.Configuration;
using TopHat.Diagnostics;
using TopHat.Handlers;
using TopHat.Transforms;

namespace TopHat.Streaming;

/// <summary>
/// Read-only stream that forwards bytes from an upstream response stream verbatim while feeding
/// an inline parser (SSE or WholeBody, depending on <see cref="TeeMode"/>). Records
/// <c>tophat.request.duration</c> and <c>tophat.stream.outcome</c> exactly once per instance at
/// stream close — whichever of EOF, async dispose, or error fires first wins. If the response is
/// synchronously disposed, response-side transforms DO NOT fire (see the sync/async finalization
/// split below).
/// </summary>
internal sealed class TeeStream : Stream
{
    /// <summary>Maximum number of usage-bearing SSE frames retained for response-transform observation.</summary>
    private const int UsageObservationCap = 1024;

    private readonly Stream _inner;
    private readonly TeeMode _mode;
    private readonly TopHatRequestContext _context;
    private readonly int _statusCode;
    private readonly SseEventReader? _sseReader;
    private readonly UsageRecorder? _recorder;
    private readonly ILogger _logger;
    private readonly long _wholeBodyCap;
    private readonly Func<TeeStream, CancellationToken, ValueTask>? _asyncFinalizationCallback;
    private readonly Action<TeeStream>? _syncFinalizationCallback;
    private readonly List<SseObservation>? _observations;

    private ArrayBufferWriter<byte>? _wholeBodyBuffer;
    private bool _wholeBodyOverCap;
    private bool _recorded;
    private bool _parserDisabled;
    private long _bytesObserved;
    private int _usageObservationIndex;
    private bool _observationsTruncated;

    public TeeStream(
        Stream inner,
        TeeMode mode,
        TopHatRequestContext context,
        int statusCode,
        UsageRecorder? recorder,
        IOptions<TopHatOptions> options,
        ILogger logger,
        Func<TeeStream, CancellationToken, ValueTask>? asyncFinalizationCallback = null,
        Action<TeeStream>? syncFinalizationCallback = null)
    {
        this._inner = inner;
        this._mode = mode;
        this._context = context;
        this._statusCode = statusCode;
        this._recorder = recorder;
        this._logger = logger;
        this._wholeBodyCap = options.Value.MaxResponseInspectionBytes;
        this._asyncFinalizationCallback = asyncFinalizationCallback;
        this._syncFinalizationCallback = syncFinalizationCallback;

        if (mode == TeeMode.Sse)
        {
            this._observations = new List<SseObservation>();
            Action<UsageEvent> wrappedOnEvent = ev =>
            {
                recorder?.OnEvent(ev);
                this.AppendObservation(ev);
            };
            this._sseReader = new SseEventReader(context.Provider, wrappedOnEvent, this.OnParserError);
        }
        else if (mode == TeeMode.WholeBody && recorder is not null)
        {
            this._wholeBodyBuffer = new ArrayBufferWriter<byte>(4096);
        }
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Usage-bearing SSE observations accumulated during streaming (Sse mode only).</summary>
    public IReadOnlyList<SseObservation> Observations =>
        (IReadOnlyList<SseObservation>?)this._observations ?? Array.Empty<SseObservation>();

    /// <summary>Total SSE frame count (usage + non-usage). Zero when not Sse.</summary>
    public int FrameCount => this._sseReader?.FrameCount ?? 0;

    /// <summary>True iff the <see cref="UsageObservationCap"/> retention cap fired.</summary>
    public bool ObservationsTruncated => this._observationsTruncated;

    /// <summary>Tee mode selected at construction (reflects response Content-Type).</summary>
    public TeeMode Mode => this._mode;

    /// <summary>Parsed whole-body content if <see cref="TeeMode.WholeBody"/>, under cap, and parse succeeds.</summary>
    public JsonNode? WholeBody
    {
        get
        {
            if (this._mode != TeeMode.WholeBody || this._wholeBodyBuffer is null || this._wholeBodyOverCap)
            {
                return null;
            }

            try
            {
                return JsonNode.Parse(this._wholeBodyBuffer.WrittenSpan);
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }
    }

    public override void Flush() => this._inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) =>
        this.Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int n;
        try
        {
            n = this._inner.Read(buffer);
        }
        catch
        {
            this.FinalizeOnceSync(StreamOutcome.Error);
            throw;
        }

        if (n == 0)
        {
            this.FinalizeOnceSync(StreamOutcome.Eof);
            return 0;
        }

        this.ObserveChunk(buffer[..n]);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int n;
        try
        {
            n = await this._inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await this.FinalizeOnceAsync(StreamOutcome.Error, cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (n == 0)
        {
            await this.FinalizeOnceAsync(StreamOutcome.Eof, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        this.ObserveChunk(buffer.Span[..n]);
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        this.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.FinalizeOnceSync(StreamOutcome.Disposed);
            this._sseReader?.Dispose();
            this._inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await this.FinalizeOnceAsync(StreamOutcome.Disposed, CancellationToken.None).ConfigureAwait(false);
        this._sseReader?.Dispose();
        await this._inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void ObserveChunk(ReadOnlySpan<byte> chunk)
    {
        this._bytesObserved += chunk.Length;

        if (this._parserDisabled)
        {
            return;
        }

        try
        {
            switch (this._mode)
            {
                case TeeMode.Sse:
                    this._sseReader?.Write(chunk);
                    break;
                case TeeMode.WholeBody when this._wholeBodyBuffer is not null && !this._wholeBodyOverCap:
                    if (this._wholeBodyBuffer.WrittenCount + chunk.Length > this._wholeBodyCap)
                    {
                        this._wholeBodyOverCap = true;
                        this.OnParserError("framing");
                    }
                    else
                    {
                        chunk.CopyTo(this._wholeBodyBuffer.GetSpan(chunk.Length));
                        this._wholeBodyBuffer.Advance(chunk.Length);
                    }

                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            this._parserDisabled = true;
            this.OnParserError("internal");
        }
    }

    private void FinalizeOnceSync(StreamOutcome outcome)
    {
        if (this._recorded)
        {
            return;
        }

        this._recorded = true;
        this.RunParserClose();
        this.RecordMetrics(outcome);

        // Response-transform dispatch intentionally skipped on the sync path. See IResponseTransform
        // XML docs for the rationale (sync-over-async deadlock risk with user-supplied transform code).
        this._syncFinalizationCallback?.Invoke(this);
    }

    private async ValueTask FinalizeOnceAsync(StreamOutcome outcome, CancellationToken cancellationToken)
    {
        if (this._recorded)
        {
            return;
        }

        this._recorded = true;
        this.RunParserClose();
        this.RecordMetrics(outcome);

        if (this._asyncFinalizationCallback is not null)
        {
            try
            {
                await this._asyncFinalizationCallback(this, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Response-transform pipeline swallowed its own exceptions; any throw here is our
                // bug. Record as an internal parser error and move on — do not propagate.
                this.OnParserError("internal");
                TopHatLogEvents.UpstreamError(this._logger, ex, "other", "FINALIZE", null, null, this._context.Stopwatch.Elapsed.TotalMilliseconds, this._context.LocalId, this._context.UpstreamRequestId);
            }
        }
    }

    private void RunParserClose()
    {
        try
        {
            switch (this._mode)
            {
                case TeeMode.Sse:
                    this._sseReader?.Complete();
                    break;
                case TeeMode.WholeBody when this._recorder is not null && this._wholeBodyBuffer is not null && !this._wholeBodyOverCap:
                    this._recorder.ExtractFromJson(this._wholeBodyBuffer.WrittenSpan);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            this.OnParserError("internal");
        }
    }

    private void RecordMetrics(StreamOutcome outcome)
    {
        this._context.Stopwatch.Stop();
        this._context.Outcome = outcome;

        var targetTag = this._context.Target.ToString();

        TopHatMetrics.RequestDuration.Record(
            this._context.Stopwatch.Elapsed.TotalMilliseconds,
            new TagList
            {
                { "target", targetTag },
                { "status_code", this._statusCode },
            });

        TopHatMetrics.StreamOutcome.Add(1, new TagList
        {
            { "target", targetTag },
            { "outcome", outcome.ToString().ToLowerInvariant() },
        });
    }

    private void AppendObservation(UsageEvent ev)
    {
        if (this._observations is null)
        {
            return;
        }

        if (this._observations.Count >= UsageObservationCap)
        {
            this._observationsTruncated = true;
            return;
        }

        this._usageObservationIndex++;
        var eventType = this._context.Provider switch
        {
            Providers.TopHatProviderKind.Anthropic => "anthropic.usage",
            Providers.TopHatProviderKind.OpenAI => "openai.usage",
            _ => "usage",
        };

        var usageFrame = new JsonObject();
        if (ev.InputTokens is long input)
        {
            usageFrame["input_tokens"] = input;
        }

        if (ev.OutputTokens is long output)
        {
            usageFrame["output_tokens"] = output;
        }

        if (ev.CacheReadInputTokens is long cacheRead)
        {
            usageFrame["cache_read_input_tokens"] = cacheRead;
        }

        if (ev.CacheCreationInputTokens is long cacheCreation)
        {
            usageFrame["cache_creation_input_tokens"] = cacheCreation;
        }

        this._observations.Add(new SseObservation(eventType, this._usageObservationIndex, this._bytesObserved, usageFrame));
    }

    private void OnParserError(string kind)
    {
        TopHatMetrics.ParserErrors.Add(1, new TagList
        {
            { "target", this._context.Target.ToString() },
            { "kind", kind },
        });
        TopHatLogEvents.ParserError(this._logger, kind, this._context.LocalId, this._context.Target);
    }
}
