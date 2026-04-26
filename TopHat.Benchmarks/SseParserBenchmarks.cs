using BenchmarkDotNet.Attributes;
using System.Text;
using TopHat.Providers;
using TopHat.Streaming;

namespace TopHat.Benchmarks;

[MemoryDiagnoser]
public class SseParserBenchmarks
{
    private byte[] _payload = [];

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
    public int RawCopy()
    {
        using (var source = new MemoryStream(this._payload))
        {
            using (var destination = new MemoryStream(this._payload.Length))
            {
                source.CopyTo(destination);
                return (int)destination.Length;
            }
        }
    }

    [Benchmark]
    public int ParseEvents()
    {
        var count = 0;
        using (var reader = new SseEventReader(
            TopHatProviderKind.Anthropic,
            _ => count++,
            _ => { }))
        {
            reader.Write(this._payload);
            reader.Complete();
        }

        return count;
    }
}
