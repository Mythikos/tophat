using System.Text.Json.Nodes;
using TopHat.Relevance;
using TopHat.Relevance.BM25;
using TopHat.Transforms.JsonContext;
using TopHat.Transforms.JsonContext.Strategies;
using Xunit;

namespace TopHat.Tests.Transforms.JsonContext;

public sealed class StringArrayCompressionStrategyTests
{
	private static JsonCompressionContext MakeCtx(string queryContext = "") =>
		new()
		{
			QueryContext = queryContext,
			Scorer = new BM25Scorer(),
			Options = new JsonContextCompressorOptions { MaxItemsAfterCrush = 15 },
		};

	private static JsonNode?[] MakeStrings(IEnumerable<string> values) =>
		values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray();

	[Fact]
	public void Compress_SmallArray_NoModification()
	{
		var items = MakeStrings(["a", "b", "c"]);
		var (_, modified) = StringArrayCompressionStrategy.Compress(items, MakeCtx());
		Assert.False(modified);
	}

	[Fact]
	public void Compress_LargeUniformArray_ReducesCount()
	{
		var items = MakeStrings(Enumerable.Range(1, 100).Select(i => $"entry {i}: record fetched successfully"));
		var (kept, modified) = StringArrayCompressionStrategy.Compress(items, MakeCtx());
		Assert.True(modified);
		Assert.True(kept.Count < 100);
	}

	[Fact]
	public void Compress_AlwaysPreservesErrorItems()
	{
		var values = Enumerable.Repeat("ok record processed", 60)
			.Concat(["error: disk full at index 40"])
			.ToList();
		var items = MakeStrings(values);
		var (kept, _) = StringArrayCompressionStrategy.Compress(items, MakeCtx());

		var keptStrings = kept.Select(n => n!.GetValue<string>()).ToArray();
		Assert.Contains("error: disk full at index 40", keptStrings);
	}

	[Fact]
	public void Compress_DeduplicatesExactDuplicates()
	{
		// All items identical — dedup should kick in.
		var items = MakeStrings(Enumerable.Repeat("same content", 50));
		var (kept, _) = StringArrayCompressionStrategy.Compress(items, MakeCtx());
		// Should keep far fewer since they're all identical.
		Assert.True(kept.Count < 20, $"Expected dedup to reduce count, got {kept.Count}");
	}

	[Fact]
	public void Compress_PreservesFirstAndLastItems()
	{
		var values = new List<string> { "FIRST" };
		values.AddRange(Enumerable.Repeat("middle entry data content", 80));
		values.Add("LAST");
		var items = MakeStrings(values);
		var (kept, _) = StringArrayCompressionStrategy.Compress(items, MakeCtx());

		var keptStrings = kept.Select(n => n!.GetValue<string>()).ToArray();
		Assert.Contains("FIRST", keptStrings);
		Assert.Contains("LAST", keptStrings);
	}

	[Fact]
	public void Compress_PreservesAnomalouslyLongStrings()
	{
		// 70 short strings, one extremely long one — anomaly detection should keep it.
		var values = Enumerable.Repeat("short", 70).ToList();
		values.Add(new string('x', 5000));  // Far above mean length.
		var items = MakeStrings(values);
		var (kept, _) = StringArrayCompressionStrategy.Compress(items, MakeCtx());

		var keptStrings = kept.Select(n => n!.GetValue<string>()).ToArray();
		Assert.Contains(values[^1], keptStrings);
	}

	[Fact]
	public void Compress_ResultItemsAreInOriginalOrder()
	{
		// Items contain indices — verify kept items are monotonically ordered.
		var values = Enumerable.Range(1, 50).Select(i => $"entry {i:D3}: some data content").ToList();
		var items = MakeStrings(values);
		var (kept, _) = StringArrayCompressionStrategy.Compress(items, MakeCtx());

		var keptTexts = kept.Select(n => n!.GetValue<string>()).ToArray();
		Assert.Equal(keptTexts, keptTexts.OrderBy(s => s, StringComparer.Ordinal).ToArray());
	}
}
