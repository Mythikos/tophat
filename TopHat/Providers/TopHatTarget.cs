namespace TopHat.Providers;

/// <summary>
/// Classifies an intercepted request by provider + endpoint for metrics and future transform routing.
/// </summary>
public enum TopHatTarget
{
    /// <summary>Host did not match any known provider, or path did not match a known endpoint on a known host.</summary>
    Unknown,

    /// <summary>Anthropic /v1/messages — primary chat completions endpoint.</summary>
    AnthropicMessages,

    /// <summary>Anthropic /v1/messages/count_tokens — token counting.</summary>
    AnthropicCountTokens,

    /// <summary>Anthropic /v1/messages/batches and sub-paths — batch job API.</summary>
    AnthropicBatches,

    /// <summary>OpenAI /v1/chat/completions.</summary>
    OpenAIChatCompletions,

    /// <summary>OpenAI /v1/responses and sub-paths.</summary>
    OpenAIResponses,

    /// <summary>OpenAI /v1/batches.</summary>
    OpenAIBatches,
}
