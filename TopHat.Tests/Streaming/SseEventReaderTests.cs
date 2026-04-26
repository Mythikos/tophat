using System.Text;
using TopHat.Providers;
using TopHat.Streaming;
using TopHat.Tests.Support;
using Xunit;

namespace TopHat.Tests.Streaming;

public sealed class SseEventReaderTests
{
    [Fact]
    public void AnthropicStream_EmitsMessageStartAndMessageDeltaUsage()
    {
        var events = new List<UsageEvent>();
        var errors = new List<string>();
        using (var reader = new SseEventReader(TopHatProviderKind.Anthropic, events.Add, errors.Add))
        {
            reader.Write(SseFixtures.AnthropicStreamBytes);
            reader.Complete();
        }

        Assert.Empty(errors);
        Assert.Equal(2, events.Count);

        Assert.Equal(42, events[0].InputTokens);
        Assert.Equal(100, events[0].CacheCreationInputTokens);
        Assert.Equal(25, events[0].CacheReadInputTokens);
        Assert.Equal(0, events[0].OutputTokens);

        Assert.Null(events[1].InputTokens);
        Assert.Equal(17, events[1].OutputTokens);
    }

    [Fact]
    public void AnthropicStream_SplitAcrossMultipleWrites_ReassemblesCorrectly()
    {
        var events = new List<UsageEvent>();
        var errors = new List<string>();
        using (var reader = new SseEventReader(TopHatProviderKind.Anthropic, events.Add, errors.Add))
        {
            var bytes = SseFixtures.AnthropicStreamBytes;
            // Feed bytes one at a time to exercise reassembly.
            for (var i = 0; i < bytes.Length; i++)
            {
                reader.Write(bytes.AsSpan(i, 1));
            }

            reader.Complete();
        }

        Assert.Empty(errors);
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void OpenAiStream_EmitsUsageFromFinalChunk()
    {
        var events = new List<UsageEvent>();
        var errors = new List<string>();
        using (var reader = new SseEventReader(TopHatProviderKind.OpenAI, events.Add, errors.Add))
        {
            reader.Write(SseFixtures.OpenAiStreamBytes);
            reader.Complete();
        }

        Assert.Empty(errors);
        var ev = Assert.Single(events);
        Assert.Equal(10, ev.InputTokens);
        Assert.Equal(5, ev.OutputTokens);
        Assert.Equal(4, ev.CacheReadInputTokens);
    }

    [Fact]
    public void OpenAiDoneSentinel_Ignored()
    {
        var events = new List<UsageEvent>();
        var errors = new List<string>();
        using (var reader = new SseEventReader(TopHatProviderKind.OpenAI, events.Add, errors.Add))
        {
            reader.Write("data: [DONE]\n\n"u8);
            reader.Complete();
        }

        Assert.Empty(events);
        Assert.Empty(errors);
    }

    [Fact]
    public void MalformedJsonInDataPayload_RecordsEventParseError()
    {
        var events = new List<UsageEvent>();
        var errors = new List<string>();
        using (var reader = new SseEventReader(TopHatProviderKind.Anthropic, events.Add, errors.Add))
        {
            reader.Write("event: message_start\ndata: {not json\n\n"u8);
            reader.Complete();
        }

        Assert.Empty(events);
        Assert.Contains("event_parse", errors);
    }

    [Fact]
    public void OverCapWithoutFrameBoundary_ShedsAndRecordsFramingError()
    {
        var events = new List<UsageEvent>();
        var errors = new List<string>();
        using (var reader = new SseEventReader(TopHatProviderKind.Anthropic, events.Add, errors.Add))
        {
            // 80 KB of non-newline bytes — exceeds the 64 KB cap without a frame boundary.
            var junk = Encoding.UTF8.GetBytes(new string('x', 80 * 1024));
            reader.Write(junk);

            // A real frame arrives after the shedding kicks in; it should still be parsed.
            reader.Write(SseFixtures.AnthropicStreamBytes);
            reader.Complete();
        }

        Assert.Contains("framing", errors);
        // Two usage events should still be emitted from the trailing valid frames.
        Assert.NotEmpty(events);
    }

    [Fact]
    public void ReaderContinuesAfterEventParseError()
    {
        var events = new List<UsageEvent>();
        var errors = new List<string>();
        using (var reader = new SseEventReader(TopHatProviderKind.Anthropic, events.Add, errors.Add))
        {
            reader.Write("event: message_start\ndata: {bad\n\n"u8);
            reader.Write(SseFixtures.AnthropicStreamBytes);
            reader.Complete();
        }

        Assert.Contains("event_parse", errors);
        Assert.Equal(2, events.Count);
    }
}
