using System.Text;

namespace TopHat.Tests.Support;

/// <summary>
/// Hand-curated SSE byte fixtures for tests. Realistic enough to exercise parser paths without
/// pulling in full network captures.
/// </summary>
internal static class SseFixtures
{
    public const string AnthropicStream =
        "event: message_start\n" +
        "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-haiku-4-5\",\"content\":[],\"stop_reason\":null,\"usage\":{\"input_tokens\":42,\"cache_creation_input_tokens\":100,\"cache_read_input_tokens\":25,\"output_tokens\":0}}}\n\n" +
        "event: content_block_start\n" +
        "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
        "event: content_block_delta\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hello\"}}\n\n" +
        "event: content_block_delta\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\" world\"}}\n\n" +
        "event: message_delta\n" +
        "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":17}}\n\n" +
        "event: message_stop\n" +
        "data: {\"type\":\"message_stop\"}\n\n";

    public const string OpenAiStream =
        "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-4o\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\"}}]}\n\n" +
        "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-4o\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hi\"}}]}\n\n" +
        "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-4o\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
        "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-4o\",\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5,\"total_tokens\":15,\"prompt_tokens_details\":{\"cached_tokens\":4}}}\n\n" +
        "data: [DONE]\n\n";

    public const string AnthropicNonStreamingJson =
        "{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-haiku-4-5\",\"content\":[{\"type\":\"text\",\"text\":\"Hi\"}],\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":42,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":10,\"output_tokens\":5}}";

    public const string OpenAiNonStreamingJson =
        "{\"id\":\"c1\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"gpt-4o\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Hi\"}}],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":3,\"total_tokens\":15,\"prompt_tokens_details\":{\"cached_tokens\":2}}}";

    public static byte[] AnthropicStreamBytes => Encoding.UTF8.GetBytes(AnthropicStream);

    public static byte[] OpenAiStreamBytes => Encoding.UTF8.GetBytes(OpenAiStream);

    public static byte[] AnthropicNonStreamingBytes => Encoding.UTF8.GetBytes(AnthropicNonStreamingJson);

    public static byte[] OpenAiNonStreamingBytes => Encoding.UTF8.GetBytes(OpenAiNonStreamingJson);
}
