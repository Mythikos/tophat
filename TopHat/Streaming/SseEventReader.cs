using System.Buffers;
using System.Text.Json;
using TopHat.Providers;

namespace TopHat.Streaming;

/// <summary>
/// Consumes a stream of bytes from an SSE response and emits <see cref="UsageEvent"/>s for each
/// complete event carrying usage information. Non-blocking, synchronous, pooled-buffer.
/// </summary>
/// <remarks>
/// Self-shedding backpressure: if the internal reassembly buffer fills (64 KB) without a frame
/// boundary, the oldest half is dropped and a framing error is reported. The reader never blocks
/// the caller (which is <see cref="TeeStream"/>'s read path) on parser state.
/// </remarks>
internal sealed class SseEventReader : IDisposable
{
    private const int MAX_BUFFER_SIZE = 64 * 1024;

    private static readonly byte[] s_doubleNewline = "\n\n"u8.ToArray();

    private readonly TopHatProviderKind _provider;
    private readonly Action<UsageEvent> _onEvent;
    private readonly Action<string> _onParserError;

    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(4096);
    private int _count;
    private bool _disabled;
    private int _frameCount;

    public SseEventReader(TopHatProviderKind provider, Action<UsageEvent> onEvent, Action<string> onParserError)
    {
        this._provider = provider;
        this._onEvent = onEvent;
        this._onParserError = onParserError;
    }

    /// <summary>
    /// Total count of SSE frames parsed that carried a non-empty <c>data</c> payload (including
    /// non-usage frames like content deltas). Not decremented; accumulates over the lifetime of the
    /// reader.
    /// </summary>
    public int FrameCount => this._frameCount;

    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (this._disabled || bytes.IsEmpty)
        {
            return;
        }

        try
        {
            this.Append(bytes);
            this.ProcessFrames();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            this._disabled = true;
            this._onParserError("internal");
        }
    }

    public void Complete()
    {
        if (this._disabled)
        {
            return;
        }

        // Trailing frame without a terminating \n\n — treat as a final event if it parses.
        if (this._count > 0)
        {
            try
            {
                this.ParseFrame(this._buffer.AsSpan(0, this._count));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                this._onParserError("internal");
            }

            this._count = 0;
        }
    }

    public void Dispose()
    {
        if (this._buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(this._buffer);
            this._buffer = [];
            this._count = 0;
        }
    }

    private void Append(ReadOnlySpan<byte> bytes)
    {
        var required = this._count + bytes.Length;
        if (required > this._buffer.Length)
        {
            var newSize = Math.Max(this._buffer.Length * 2, Math.Min(required, MAX_BUFFER_SIZE));
            if (newSize > MAX_BUFFER_SIZE)
            {
                newSize = MAX_BUFFER_SIZE;
            }

            if (required > newSize)
            {
                // Even after growing to max, the incoming chunk won't fit the remaining free space.
                // Shed the oldest half of the buffer so we can at least accept the new bytes.
                this._onParserError("framing");
                var keep = this._count / 2;
                Buffer.BlockCopy(this._buffer, this._count - keep, this._buffer, 0, keep);
                this._count = keep;

                if (this._count + bytes.Length > this._buffer.Length)
                {
                    // Chunk is enormous; drop any bytes beyond buffer capacity and continue.
                    var available = this._buffer.Length - this._count;
                    bytes.Slice(bytes.Length - available).CopyTo(this._buffer.AsSpan(this._count));
                    this._count += available;
                    return;
                }
            }
            else
            {
                var grown = ArrayPool<byte>.Shared.Rent(newSize);
                Buffer.BlockCopy(this._buffer, 0, grown, 0, this._count);
                ArrayPool<byte>.Shared.Return(this._buffer);
                this._buffer = grown;
            }
        }

        bytes.CopyTo(this._buffer.AsSpan(this._count));
        this._count += bytes.Length;
    }

    private void ProcessFrames()
    {
        while (true)
        {
            var span = this._buffer.AsSpan(0, this._count);
            var boundary = span.IndexOf(s_doubleNewline);
            if (boundary < 0)
            {
                if (this._count >= MAX_BUFFER_SIZE)
                {
                    // No frame boundary in 64 KB — shed half and report framing error.
                    this._onParserError("framing");
                    var keep = this._count / 2;
                    Buffer.BlockCopy(this._buffer, this._count - keep, this._buffer, 0, keep);
                    this._count = keep;
                }

                return;
            }

            this.ParseFrame(span[..boundary]);

            var consumed = boundary + s_doubleNewline.Length;
            var remaining = this._count - consumed;
            if (remaining > 0)
            {
                Buffer.BlockCopy(this._buffer, consumed, this._buffer, 0, remaining);
            }

            this._count = remaining;
        }
    }

    private void ParseFrame(ReadOnlySpan<byte> frame)
    {
        ReadOnlySpan<byte> eventName = default;
        ReadOnlySpan<byte> dataPayload = default;

        while (!frame.IsEmpty)
        {
            var newline = frame.IndexOf((byte)'\n');
            ReadOnlySpan<byte> line;
            if (newline < 0)
            {
                line = frame;
                frame = default;
            }
            else
            {
                line = frame[..newline];
                frame = frame[(newline + 1)..];
            }

            if (line.IsEmpty || line[0] == (byte)':')
            {
                continue;
            }

            var colon = line.IndexOf((byte)':');
            if (colon < 0)
            {
                continue;
            }

            var field = line[..colon];
            var value = line[(colon + 1)..];
            if (!value.IsEmpty && value[0] == (byte)' ')
            {
                value = value[1..];
            }

            if (field.SequenceEqual("event"u8))
            {
                eventName = value;
            }
            else if (field.SequenceEqual("data"u8))
            {
                dataPayload = value;
            }
        }

        if (dataPayload.IsEmpty)
        {
            return;
        }

        this._frameCount++;

        try
        {
            if (this._provider == TopHatProviderKind.Anthropic)
            {
                if (AnthropicSseExtractor.TryParse(eventName, dataPayload, out var ev))
                {
                    this._onEvent(ev);
                }
            }
            else if (this._provider == TopHatProviderKind.OpenAI)
            {
                if (OpenAiSseExtractor.TryParse(dataPayload, out var ev))
                {
                    this._onEvent(ev);
                }
            }
        }
        catch (JsonException)
        {
            this._onParserError("event_parse");
        }
    }
}

/// <summary>
/// Anthropic SSE usage extraction. <c>message_start</c> carries input + cache tokens;
/// <c>message_delta</c> carries cumulative output tokens. <c>message_stop</c> is a lifecycle
/// marker only — no counting work is done on it. Finalization happens via
/// <see cref="SseEventReader.Complete"/> at stream EOF, not at <c>message_stop</c>.
/// </summary>
internal static class AnthropicSseExtractor
{
    public static bool TryParse(ReadOnlySpan<byte> eventName, ReadOnlySpan<byte> dataPayload, out UsageEvent ev)
    {
        ev = default;

        if (eventName.SequenceEqual("message_start"u8))
        {
            return TryParseMessageStart(dataPayload, out ev);
        }

        if (eventName.SequenceEqual("message_delta"u8))
        {
            return TryParseMessageDelta(dataPayload, out ev);
        }

        // message_stop, content_block_*, ping, etc. carry no usage — intentional no-op.
        return false;
    }

    private static bool TryParseMessageStart(ReadOnlySpan<byte> json, out UsageEvent ev)
    {
        ev = default;
        var reader = new Utf8JsonReader(json);

        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals("message"u8))
            {
                continue;
            }

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals("usage"u8))
                {
                    reader.Skip();
                    continue;
                }

                return TryParseUsageObject(ref reader, out ev);
            }

            return false;
        }

        return false;
    }

    private static bool TryParseMessageDelta(ReadOnlySpan<byte> json, out UsageEvent ev)
    {
        ev = default;
        var reader = new Utf8JsonReader(json);

        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals("usage"u8))
            {
                continue;
            }

            return TryParseUsageObject(ref reader, out ev);
        }

        return false;
    }

    private static bool TryParseUsageObject(ref Utf8JsonReader reader, out UsageEvent ev)
    {
        ev = default;
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return false;
        }

        long? input = null;
        long? output = null;
        long? cacheRead = null;
        long? cacheCreation = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            if (reader.ValueTextEquals("input_tokens"u8))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.Number)
                {
                    input = reader.GetInt64();
                }
            }
            else if (reader.ValueTextEquals("output_tokens"u8))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.Number)
                {
                    output = reader.GetInt64();
                }
            }
            else if (reader.ValueTextEquals("cache_read_input_tokens"u8))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.Number)
                {
                    cacheRead = reader.GetInt64();
                }
            }
            else if (reader.ValueTextEquals("cache_creation_input_tokens"u8))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.Number)
                {
                    cacheCreation = reader.GetInt64();
                }
            }
            else
            {
                reader.Skip();
            }
        }

        if (input is null && output is null && cacheRead is null && cacheCreation is null)
        {
            return false;
        }

        ev = new UsageEvent(input, output, cacheRead, cacheCreation);
        return true;
    }
}

/// <summary>
/// OpenAI SSE usage extraction. The <c>chunk.usage</c> block appears on the final chunk when
/// <c>stream_options.include_usage</c> was set on the request. <c>[DONE]</c> data payload is a
/// stream terminator, not JSON.
/// </summary>
internal static class OpenAiSseExtractor
{
    private static ReadOnlySpan<byte> DoneSentinel => "[DONE]"u8;

    public static bool TryParse(ReadOnlySpan<byte> dataPayload, out UsageEvent ev)
    {
        ev = default;

        if (dataPayload.SequenceEqual(DoneSentinel))
        {
            return false;
        }

        var reader = new Utf8JsonReader(dataPayload);
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals("usage"u8))
            {
                continue;
            }

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            long? prompt = null;
            long? completion = null;
            long? cached = null;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                // /v1/chat/completions: prompt_tokens / completion_tokens / prompt_tokens_details.cached_tokens
                // /v1/responses:        input_tokens  / output_tokens     / input_tokens_details.cached_tokens
                if (reader.ValueTextEquals("prompt_tokens"u8) || reader.ValueTextEquals("input_tokens"u8))
                {
                    if (reader.Read() && reader.TokenType == JsonTokenType.Number)
                    {
                        prompt = reader.GetInt64();
                    }
                }
                else if (reader.ValueTextEquals("completion_tokens"u8) || reader.ValueTextEquals("output_tokens"u8))
                {
                    if (reader.Read() && reader.TokenType == JsonTokenType.Number)
                    {
                        completion = reader.GetInt64();
                    }
                }
                else if (reader.ValueTextEquals("prompt_tokens_details"u8) || reader.ValueTextEquals("input_tokens_details"u8))
                {
                    if (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                    {
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                        {
                            if (reader.TokenType == JsonTokenType.PropertyName &&
                                reader.ValueTextEquals("cached_tokens"u8))
                            {
                                if (reader.Read() && reader.TokenType == JsonTokenType.Number)
                                {
                                    cached = reader.GetInt64();
                                }
                            }
                            else
                            {
                                reader.Skip();
                            }
                        }
                    }
                }
                else
                {
                    reader.Skip();
                }
            }

            if (prompt is null && completion is null && cached is null)
            {
                return false;
            }

            ev = new UsageEvent(prompt, completion, cached, null);
            return true;
        }

        return false;
    }
}
