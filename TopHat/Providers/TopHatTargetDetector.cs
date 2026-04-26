using Microsoft.Extensions.Options;
using TopHat.Configuration;

namespace TopHat.Providers;

/// <summary>
/// Classifies requests by provider (host-based) and endpoint (host + path).
/// Provider detection drives URI rewriting; target detection drives metrics tags.
/// These are deliberately separate concerns — a rewrite applies to unknown targets on a known host too.
/// </summary>
internal sealed class TopHatTargetDetector
{
    private const string ANTHROPIC_DEFAULT_HOST = "api.anthropic.com";
    private const string OPENAI_DEFAULT_HOST = "api.openai.com";

    private readonly IOptions<TopHatOptions> _options;

    public TopHatTargetDetector(IOptions<TopHatOptions> options)
    {
        this._options = options;
    }

    public TopHatProviderKind DetectProvider(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var host = Normalize(request.RequestUri?.Host);
        if (host is null)
        {
            return TopHatProviderKind.Other;
        }

        if (HostMatches(host, ANTHROPIC_DEFAULT_HOST) || HostMatches(host, Normalize(this._options.Value.AnthropicBaseUrl?.Host)))
        {
            return TopHatProviderKind.Anthropic;
        }

        if (HostMatches(host, OPENAI_DEFAULT_HOST) || HostMatches(host, Normalize(this._options.Value.OpenAiBaseUrl?.Host)))
        {
            return TopHatProviderKind.OpenAI;
        }

        return TopHatProviderKind.Other;
    }

    public TopHatTarget DetectTarget(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var provider = this.DetectProvider(request);
        if (provider == TopHatProviderKind.Other)
        {
            return TopHatTarget.Unknown;
        }

        var path = request.RequestUri?.AbsolutePath ?? string.Empty;

        return provider switch
        {
            TopHatProviderKind.Anthropic => ClassifyAnthropic(path),
            TopHatProviderKind.OpenAI => ClassifyOpenAI(path),
            _ => TopHatTarget.Unknown,
        };
    }

    private static TopHatTarget ClassifyAnthropic(string path) => path switch
    {
        "/v1/messages" => TopHatTarget.AnthropicMessages,
        "/v1/messages/count_tokens" => TopHatTarget.AnthropicCountTokens,
        _ when path.StartsWith("/v1/messages/batches", StringComparison.Ordinal) => TopHatTarget.AnthropicBatches,
        _ => TopHatTarget.Unknown,
    };

    private static TopHatTarget ClassifyOpenAI(string path) => path switch
    {
        "/v1/chat/completions" => TopHatTarget.OpenAIChatCompletions,
        _ when path.StartsWith("/v1/responses", StringComparison.Ordinal) => TopHatTarget.OpenAIResponses,
        _ when path.StartsWith("/v1/batches", StringComparison.Ordinal) => TopHatTarget.OpenAIBatches,
        _ => TopHatTarget.Unknown,
    };

    private static string? Normalize(string? host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return null;
        }

        return host.EndsWith('.') ? host[..^1] : host;
    }

    private static bool HostMatches(string host, string? candidate)
    {
        return candidate is not null && string.Equals(host, candidate, StringComparison.OrdinalIgnoreCase);
    }
}
