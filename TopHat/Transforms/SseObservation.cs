using System.Text.Json.Nodes;

namespace TopHat.Transforms;

/// <summary>
/// A single SSE frame observation surfaced to <see cref="IResponseTransform"/> implementations.
/// Only usage-bearing frames (those carrying a parseable <c>usage</c> object) are retained; other
/// frames (content deltas, pings, lifecycle markers) are counted toward
/// <see cref="ResponseTransformContext.ObservedEventCount"/> but NOT represented here.
/// </summary>
/// <remarks>
/// <para><see cref="UsageFrame"/> is the parsed usage object as a <see cref="JsonNode"/> (typically a
/// <see cref="JsonObject"/>). Exact shape is provider-specific — callers should be defensive when
/// reading fields. For Anthropic: <c>message_start</c> or <c>message_delta</c>. For OpenAI: the
/// trailing chunk carrying <c>usage</c>.</para>
/// <para>This record is populated only when <c>Mode == TeeMode.Sse</c>. It is stable public API.</para>
/// </remarks>
public sealed record SseObservation(string EventType, int Index, long ByteOffset, JsonNode UsageFrame);
