using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text;
using TopHat.Configuration;
using TopHat.Handlers;
using TopHat.Providers;
using TopHat.Streaming;

namespace TopHat.Benchmarks;

[MemoryDiagnoser]
public class TeeStreamBenchmarks
{
    private byte[] _payload = [];

    private readonly IOptions<TopHatOptions> _options = Options.Create(new TopHatOptions());

    [GlobalSetup]
    public void Setup()
    {
        const string oneEvent = "event: message_delta\n" + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":null},\"usage\":{\"output_tokens\":17}}\n\n";
        var sb = new StringBuilder();
        while (sb.Length < 1024 * 1024)
        {
            sb.Append(oneEvent);
        }

        this._payload = Encoding.UTF8.GetBytes(sb.ToString());
    }

    [Benchmark(Baseline = true)]
    public long Passthrough()
    {
        var context = new TopHatRequestContext
        {
            LocalId = "b",
            Stopwatch = Stopwatch.StartNew(),
            Provider = TopHatProviderKind.Anthropic,
            Target = TopHatTarget.AnthropicMessages,
        };
        using (var inner = new MemoryStream(this._payload))
        {
            using (var tee = new TeeStream(inner, TeeMode.Passthrough, context, 200, null, this._options, NullLogger.Instance))
            {
                using (var dest = new MemoryStream())
                {
                    tee.CopyTo(dest);
                    return dest.Length;
                }
            }
        }
    }

    [Benchmark]
    public long SseObserved()
    {
        var context = new TopHatRequestContext
        {
            LocalId = "b",
            Stopwatch = Stopwatch.StartNew(),
            Provider = TopHatProviderKind.Anthropic,
            Target = TopHatTarget.AnthropicMessages,
            Model = "test",
        };
        var recorder = new UsageRecorder(TopHatProviderKind.Anthropic, "AnthropicMessages", "test");
        using (var inner = new MemoryStream(this._payload))
        {
            using (var tee = new TeeStream(inner, TeeMode.Sse, context, 200, recorder, this._options, NullLogger.Instance))
            {
                using (var dest = new MemoryStream())
                {
                    tee.CopyTo(dest);
                    return dest.Length;
                }
            }
        }
    }
}
