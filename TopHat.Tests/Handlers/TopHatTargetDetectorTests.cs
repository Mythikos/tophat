using Microsoft.Extensions.Options;
using TopHat.Configuration;
using TopHat.Providers;
using Xunit;

namespace TopHat.Tests.Handlers;

public sealed class TopHatTargetDetectorTests
{
    [Theory]
    [InlineData("https://api.anthropic.com/v1/messages", nameof(TopHatTarget.AnthropicMessages))]
    [InlineData("https://api.anthropic.com/v1/messages/count_tokens", nameof(TopHatTarget.AnthropicCountTokens))]
    [InlineData("https://api.anthropic.com/v1/messages/batches", nameof(TopHatTarget.AnthropicBatches))]
    [InlineData("https://api.anthropic.com/v1/messages/batches/abc123/results", nameof(TopHatTarget.AnthropicBatches))]
    [InlineData("https://api.anthropic.com/v1/something-new", nameof(TopHatTarget.Unknown))]
    [InlineData("https://API.ANTHROPIC.COM/v1/messages", nameof(TopHatTarget.AnthropicMessages))]
    [InlineData("https://api.anthropic.com./v1/messages", nameof(TopHatTarget.AnthropicMessages))]
    [InlineData("https://api.openai.com/v1/chat/completions", nameof(TopHatTarget.OpenAIChatCompletions))]
    [InlineData("https://api.openai.com/v1/responses", nameof(TopHatTarget.OpenAIResponses))]
    [InlineData("https://api.openai.com/v1/responses/abc", nameof(TopHatTarget.OpenAIResponses))]
    [InlineData("https://api.openai.com/v1/batches", nameof(TopHatTarget.OpenAIBatches))]
    [InlineData("https://api.openai.com/v1/assistants", nameof(TopHatTarget.Unknown))]
    [InlineData("https://example.com/v1/messages", nameof(TopHatTarget.Unknown))]
    public void DetectsTargetOnDefaultHosts(string url, string expectedTargetName)
    {
        var expected = Enum.Parse<TopHatTarget>(expectedTargetName);
        var detector = new TopHatTargetDetector(Options.Create(new TopHatOptions()));
        using (var req = new HttpRequestMessage(HttpMethod.Post, url))
        {
            Assert.Equal(expected, detector.DetectTarget(req));
        }
    }

    [Theory]
    [InlineData("https://api.anthropic.com/v1/messages", "Anthropic")]
    [InlineData("https://api.openai.com/v1/chat/completions", "OpenAI")]
    [InlineData("https://example.com/v1/messages", "Other")]
    public void DetectsProviderOnDefaultHosts(string url, string expectedProviderName)
    {
        var expected = Enum.Parse<TopHatProviderKind>(expectedProviderName);
        var detector = new TopHatTargetDetector(Options.Create(new TopHatOptions()));
        using (var req = new HttpRequestMessage(HttpMethod.Post, url))
        {
            Assert.Equal(expected, detector.DetectProvider(req));
        }
    }

    [Fact]
    public void CustomAnthropicHostIsDetectedAsAnthropic()
    {
        var opts = new TopHatOptions { AnthropicBaseUrl = new Uri("https://gateway.internal:9000") };
        var detector = new TopHatTargetDetector(Options.Create(opts));
        using (var req = new HttpRequestMessage(HttpMethod.Post, "https://gateway.internal:9000/v1/messages"))
        {
            Assert.Equal(TopHatProviderKind.Anthropic, detector.DetectProvider(req));
            Assert.Equal(TopHatTarget.AnthropicMessages, detector.DetectTarget(req));
        }
    }

    [Fact]
    public void CustomOpenAiHostIsDetectedAsOpenAi()
    {
        var opts = new TopHatOptions { OpenAiBaseUrl = new Uri("https://openai.internal") };
        var detector = new TopHatTargetDetector(Options.Create(opts));
        using (var req = new HttpRequestMessage(HttpMethod.Post, "https://openai.internal/v1/chat/completions"))
        {
            Assert.Equal(TopHatProviderKind.OpenAI, detector.DetectProvider(req));
            Assert.Equal(TopHatTarget.OpenAIChatCompletions, detector.DetectTarget(req));
        }
    }
}
