using System.Diagnostics;
using System.Text.Json;
using TopHat.Diagnostics;
using TopHat.Providers;

namespace TopHat.Streaming;

/// <summary>
/// Translates <see cref="UsageEvent"/>s (from SSE) or a single JSON payload (from WholeBody) into
/// counter increments on the token metrics. Tracks per-stream cumulative state so SSE events
/// reporting cumulative counts (Anthropic's <c>message_delta</c>) are emitted as deltas, not
/// double-counted.
/// </summary>
internal sealed class UsageRecorder
{
    private readonly TopHatProviderKind _provider;
    private readonly string _targetTag;
    private readonly string _model;

    private long _inputSeen;
    private long _outputSeen;
    private long _cacheReadSeen;
    private long _cacheCreationSeen;

    public UsageRecorder(TopHatProviderKind provider, string targetTag, string model)
    {
        this._provider = provider;
        this._targetTag = targetTag;
        this._model = model;
    }

    public void OnEvent(UsageEvent ev)
    {
        this.RecordDelta(ev.InputTokens, ref this._inputSeen, TopHatMetrics.TokensInput);
        this.RecordDelta(ev.OutputTokens, ref this._outputSeen, TopHatMetrics.TokensOutput);
        this.RecordDelta(ev.CacheReadInputTokens, ref this._cacheReadSeen, TopHatMetrics.TokensCacheRead);
        this.RecordDelta(ev.CacheCreationInputTokens, ref this._cacheCreationSeen, TopHatMetrics.TokensCacheCreation);
    }

    private void RecordDelta(long? reported, ref long seen, System.Diagnostics.Metrics.Counter<long> counter)
    {
        if (reported is null)
        {
            return;
        }

        var delta = reported.Value - seen;
        if (delta <= 0)
        {
            return;
        }

        counter.Add(delta, new TagList
        {
            { "target", this._targetTag },
            { "model", this._model },
        });

        seen = reported.Value;
    }

    /// <summary>
    /// One-shot extraction from a non-streaming JSON response body. Both Anthropic and OpenAI
    /// place <c>usage</c> at a predictable location; we scan once and record each present field
    /// as an absolute count (no delta tracking).
    /// </summary>
    public void ExtractFromJson(ReadOnlySpan<byte> body)
    {
        var ev = this._provider switch
        {
            TopHatProviderKind.Anthropic => TryExtractAnthropic(body),
            TopHatProviderKind.OpenAI => TryExtractOpenAi(body),
            _ => null,
        };

        if (ev is null)
        {
            return;
        }

        this.OnEvent(ev.Value);
    }

    private static UsageEvent? TryExtractAnthropic(ReadOnlySpan<byte> body)
    {
        try
        {
            var reader = new Utf8JsonReader(body);
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("usage"u8))
                {
                    return TryParseAnthropicUsage(ref reader);
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static UsageEvent? TryParseAnthropicUsage(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return null;
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

            if (reader.ValueTextEquals("input_tokens"u8) && reader.Read() && reader.TokenType == JsonTokenType.Number)
            {
                input = reader.GetInt64();
            }
            else if (reader.ValueTextEquals("output_tokens"u8) && reader.Read() && reader.TokenType == JsonTokenType.Number)
            {
                output = reader.GetInt64();
            }
            else if (reader.ValueTextEquals("cache_read_input_tokens"u8) && reader.Read() && reader.TokenType == JsonTokenType.Number)
            {
                cacheRead = reader.GetInt64();
            }
            else if (reader.ValueTextEquals("cache_creation_input_tokens"u8) && reader.Read() && reader.TokenType == JsonTokenType.Number)
            {
                cacheCreation = reader.GetInt64();
            }
            else
            {
                reader.Skip();
            }
        }

        if (input is null && output is null && cacheRead is null && cacheCreation is null)
        {
            return null;
        }

        return new UsageEvent(input, output, cacheRead, cacheCreation);
    }

    private static UsageEvent? TryExtractOpenAi(ReadOnlySpan<byte> body)
    {
        try
        {
            var reader = new Utf8JsonReader(body);
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("usage"u8))
                {
                    return TryParseOpenAiUsage(ref reader);
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static UsageEvent? TryParseOpenAiUsage(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return null;
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
            if ((reader.ValueTextEquals("prompt_tokens"u8) || reader.ValueTextEquals("input_tokens"u8)) && reader.Read() && reader.TokenType == JsonTokenType.Number)
            {
                prompt = reader.GetInt64();
            }
            else if ((reader.ValueTextEquals("completion_tokens"u8) || reader.ValueTextEquals("output_tokens"u8)) && reader.Read() && reader.TokenType == JsonTokenType.Number)
            {
                completion = reader.GetInt64();
            }
            else if ((reader.ValueTextEquals("prompt_tokens_details"u8) || reader.ValueTextEquals("input_tokens_details"u8)) && reader.Read() && reader.TokenType == JsonTokenType.StartObject)
            {
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType == JsonTokenType.PropertyName &&
                        reader.ValueTextEquals("cached_tokens"u8) &&
                        reader.Read() &&
                        reader.TokenType == JsonTokenType.Number)
                    {
                        cached = reader.GetInt64();
                    }
                    else
                    {
                        reader.Skip();
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
            return null;
        }

        return new UsageEvent(prompt, completion, cached, null);
    }
}
