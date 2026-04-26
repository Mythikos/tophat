using System.Text;
using TopHat.Providers;
using TopHat.Streaming;
using TopHat.Tests.Support;
using Xunit;

namespace TopHat.Tests.Streaming;

public sealed class UsageRecorderTests
{
    [Fact]
    public void AnthropicStream_EmitsDeltasNotCumulativeDoubleCounts()
    {
        using (var capture = new MetricsCapture())
        {
            var recorder = new UsageRecorder(TopHatProviderKind.Anthropic, "AnthropicMessages", "claude-haiku-4-5");

            // Simulate two message_delta events where output_tokens is cumulative: 5, then 17.
            recorder.OnEvent(new UsageEvent(42, null, 25, 100));
            recorder.OnEvent(new UsageEvent(null, 5, null, null));
            recorder.OnEvent(new UsageEvent(null, 17, null, null));

            var input = Sum(capture, "tophat.tokens.input");
            var output = Sum(capture, "tophat.tokens.output");
            var cacheRead = Sum(capture, "tophat.tokens.cache_read");
            var cacheCreation = Sum(capture, "tophat.tokens.cache_creation");

            Assert.Equal(42, input);
            Assert.Equal(17, output);  // 5 + 12 delta, not 5 + 17
            Assert.Equal(25, cacheRead);
            Assert.Equal(100, cacheCreation);
        }
    }

    [Fact]
    public void AnthropicNonStreamingJson_PopulatesCountersOnce()
    {
        using (var capture = new MetricsCapture())
        {
            var recorder = new UsageRecorder(TopHatProviderKind.Anthropic, "AnthropicMessages", "claude-haiku-4-5");

            recorder.ExtractFromJson(SseFixtures.AnthropicNonStreamingBytes);

            Assert.Equal(42, Sum(capture, "tophat.tokens.input"));
            Assert.Equal(5, Sum(capture, "tophat.tokens.output"));
            Assert.Equal(10, Sum(capture, "tophat.tokens.cache_read"));
        }
    }

    [Fact]
    public void OpenAiNonStreamingJson_PopulatesCountersOnce()
    {
        using (var capture = new MetricsCapture())
        {
            var recorder = new UsageRecorder(TopHatProviderKind.OpenAI, "OpenAIChatCompletions", "gpt-4o");

            recorder.ExtractFromJson(SseFixtures.OpenAiNonStreamingBytes);

            Assert.Equal(12, Sum(capture, "tophat.tokens.input"));
            Assert.Equal(3, Sum(capture, "tophat.tokens.output"));
            Assert.Equal(2, Sum(capture, "tophat.tokens.cache_read"));
        }
    }

    [Fact]
    public void RepeatedSameValue_RecordsNoDuplicate()
    {
        using (var capture = new MetricsCapture())
        {
            var recorder = new UsageRecorder(TopHatProviderKind.Anthropic, "AnthropicMessages", "x");

            recorder.OnEvent(new UsageEvent(10, null, null, null));
            recorder.OnEvent(new UsageEvent(10, null, null, null));  // same; should not double-count

            Assert.Equal(10, Sum(capture, "tophat.tokens.input"));
        }
    }

    [Fact]
    public void ResponsesEndpoint_ExtractsCachedTokensFromInputTokensDetails()
    {
        // /v1/responses returns a distinct usage shape from /v1/chat/completions:
        //   - input_tokens instead of prompt_tokens
        //   - output_tokens instead of completion_tokens
        //   - input_tokens_details.cached_tokens instead of prompt_tokens_details.cached_tokens
        // Confirmed empirically via the --probe-responses spike during M5 implementation.
        using (var capture = new MetricsCapture())
        {
            var recorder = new UsageRecorder(TopHatProviderKind.OpenAI, "OpenAIResponses", "gpt-4o-mini");
            var json = "{\"id\":\"resp_1\",\"usage\":{\"input_tokens\":1024,\"input_tokens_details\":{\"cached_tokens\":512},\"output_tokens\":64,\"output_tokens_details\":{\"reasoning_tokens\":0},\"total_tokens\":1088}}";

            recorder.ExtractFromJson(Encoding.UTF8.GetBytes(json));

            Assert.Equal(1024, Sum(capture, "tophat.tokens.input"));
            Assert.Equal(64, Sum(capture, "tophat.tokens.output"));
            Assert.Equal(512, Sum(capture, "tophat.tokens.cache_read"));
        }
    }

    [Fact]
    public void ChatCompletionsEndpoint_StillExtractsFromPromptTokensDetails()
    {
        // Regression guard for the original shape after M5's incidental fix added the alternatives.
        using (var capture = new MetricsCapture())
        {
            var recorder = new UsageRecorder(TopHatProviderKind.OpenAI, "OpenAIChatCompletions", "gpt-4o");
            var json = "{\"id\":\"c1\",\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":20,\"total_tokens\":120,\"prompt_tokens_details\":{\"cached_tokens\":50}}}";

            recorder.ExtractFromJson(Encoding.UTF8.GetBytes(json));

            Assert.Equal(100, Sum(capture, "tophat.tokens.input"));
            Assert.Equal(20, Sum(capture, "tophat.tokens.output"));
            Assert.Equal(50, Sum(capture, "tophat.tokens.cache_read"));
        }
    }

    private static double Sum(MetricsCapture capture, string instrumentName)
    {
        var total = 0d;
        foreach (var r in capture.ForInstrument(instrumentName))
        {
            total += r.Value;
        }

        return total;
    }
}
