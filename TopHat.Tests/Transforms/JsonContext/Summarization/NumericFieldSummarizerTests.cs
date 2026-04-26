using Microsoft.Extensions.Options;
using System.Text.Json.Nodes;
using TopHat.Transforms.JsonContext.Summarization;
using Xunit;

namespace TopHat.Tests.Transforms.JsonContext.Summarization;

public sealed class NumericFieldSummarizerTests
{
	private static NumericFieldSummarizer MakeSummarizer(NumericFieldSummarizerOptions? opts = null) =>
		new (Options.Create(opts ?? new NumericFieldSummarizerOptions()));

	private static SummarizationContext MakeContext(int dropped) =>
		new () { OriginalCount = dropped + 5, KeptCount = 5 };

	[Fact]
	public void Summarize_EmptyDropped_ReturnsNull()
	{
		var summarizer = MakeSummarizer();
		var result = summarizer.Summarize(Array.Empty<JsonObject>(), new SummarizationContext { OriginalCount = 5, KeptCount = 5 });
		Assert.Null(result);
	}

	[Fact]
	public void Summarize_NoNumericFields_ReturnsNull()
	{
		var dropped = new List<JsonObject>();

		for (var i = 0; i < 20; i++)
		{
			dropped.Add(new JsonObject { ["status"] = "ok", ["name"] = $"item-{i}" });
		}

		var result = MakeSummarizer().Summarize(dropped, MakeContext(dropped.Count));
		Assert.Null(result);
	}

	[Fact]
	public void Summarize_EmitsBasicStats()
	{
		var dropped = new List<JsonObject>();
		var values = new[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };

		foreach (var v in values)
		{
			dropped.Add(new JsonObject { ["latency_ms"] = v });
		}

		var result = MakeSummarizer().Summarize(dropped, MakeContext(dropped.Count));

		Assert.NotNull(result);
		Assert.Contains("latency_ms:", result);
		Assert.Contains("n=10", result);
		Assert.Contains("min=10", result);
		Assert.Contains("max=100", result);
		Assert.Contains("mean=55", result);
	}

	[Fact]
	public void Summarize_EmitsPercentiles()
	{
		var dropped = new List<JsonObject>();

		for (var i = 1; i <= 100; i++)
		{
			dropped.Add(new JsonObject { ["value"] = i });
		}

		var result = MakeSummarizer().Summarize(dropped, MakeContext(dropped.Count));

		Assert.NotNull(result);
		Assert.Contains("p50=", result);
		Assert.Contains("p90=", result);
		Assert.Contains("p99=", result);
	}

	[Fact]
	public void Summarize_SkipsFieldsBelowMinSampleSize()
	{
		var dropped = new List<JsonObject>
		{
			new () { ["rare"] = 1 },
			new () { ["rare"] = 2 },
			// 8 items without the field so 'rare' has only 2 samples.
		};

		for (var i = 0; i < 8; i++)
		{
			dropped.Add(new JsonObject { ["other"] = "x" });
		}

		var opts = new NumericFieldSummarizerOptions { MinSampleSize = 5 };
		var result = MakeSummarizer(opts).Summarize(dropped, MakeContext(dropped.Count));

		Assert.Null(result);
	}

	[Fact]
	public void Summarize_RespectsDenyList()
	{
		var dropped = new List<JsonObject>();

		for (var i = 1; i <= 10; i++)
		{
			dropped.Add(new JsonObject { ["id"] = i, ["timestamp"] = 1700000000 + i, ["latency_ms"] = i * 10 });
		}

		var result = MakeSummarizer().Summarize(dropped, MakeContext(dropped.Count));

		Assert.NotNull(result);
		Assert.Contains("latency_ms:", result);
		Assert.DoesNotContain("id:", result);
		Assert.DoesNotContain("timestamp:", result);
	}

	[Fact]
	public void Summarize_RespectsAllowList()
	{
		var dropped = new List<JsonObject>();

		for (var i = 1; i <= 10; i++)
		{
			dropped.Add(new JsonObject { ["latency_ms"] = i * 10, ["duration_ms"] = i * 5 });
		}

		var opts = new NumericFieldSummarizerOptions { FieldAllowList = new[] { "latency_ms" } };
		var result = MakeSummarizer(opts).Summarize(dropped, MakeContext(dropped.Count));

		Assert.NotNull(result);
		Assert.Contains("latency_ms:", result);
		Assert.DoesNotContain("duration_ms:", result);
	}

	[Fact]
	public void Summarize_MultipleFieldsOrderedBySampleCount()
	{
		var dropped = new List<JsonObject>();

		// 'a' has 20 samples, 'b' has 10.
		for (var i = 0; i < 20; i++)
		{
			dropped.Add(new JsonObject { ["a"] = i });
		}

		for (var i = 0; i < 10; i++)
		{
			dropped.Add(new JsonObject { ["b"] = i });
		}

		var result = MakeSummarizer().Summarize(dropped, MakeContext(dropped.Count));

		Assert.NotNull(result);
		var aIdx = result.IndexOf("a:", StringComparison.Ordinal);
		var bIdx = result.IndexOf("b:", StringComparison.Ordinal);
		Assert.True(aIdx >= 0);
		Assert.True(bIdx >= 0);
		Assert.True(aIdx < bIdx, "'a' (20 samples) should appear before 'b' (10 samples)");
	}

	[Fact]
	public void Summarize_RespectsMaxFields()
	{
		var dropped = new List<JsonObject>();

		for (var i = 0; i < 20; i++)
		{
			dropped.Add(new JsonObject
			{
				["f1"] = i,
				["f2"] = i,
				["f3"] = i,
				["f4"] = i,
			});
		}

		var opts = new NumericFieldSummarizerOptions { MaxFields = 2 };
		var result = MakeSummarizer(opts).Summarize(dropped, MakeContext(dropped.Count));

		Assert.NotNull(result);
		var fieldCount = new[] { "f1:", "f2:", "f3:", "f4:" }.Count(f => result.Contains(f, StringComparison.Ordinal));
		Assert.Equal(2, fieldCount);
	}

	[Fact]
	public void Summarize_IgnoresNonNumericValues()
	{
		var dropped = new List<JsonObject>();

		for (var i = 0; i < 15; i++)
		{
			// Half numeric, half string.
			dropped.Add(new JsonObject { ["val"] = i % 2 == 0 ? JsonValue.Create(i) : JsonValue.Create("n/a") });
		}

		var result = MakeSummarizer().Summarize(dropped, MakeContext(dropped.Count));

		Assert.NotNull(result);
		Assert.Contains("val:", result);
		Assert.Contains("n=8", result);
	}
}
