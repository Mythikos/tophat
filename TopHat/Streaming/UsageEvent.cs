namespace TopHat.Streaming;

/// <summary>
/// Partial usage report from a provider (SSE event or final JSON). Fields are nullable because
/// events usually carry only a subset — Anthropic's <c>message_start</c> has input + cache tokens,
/// <c>message_delta</c> has cumulative output tokens, etc. Merging/delta-tracking is handled by
/// <see cref="UsageRecorder"/>, not here.
/// </summary>
internal readonly record struct UsageEvent(long? InputTokens, long? OutputTokens, long? CacheReadInputTokens, long? CacheCreationInputTokens);
