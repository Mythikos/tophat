using System.Text.Json.Nodes;
using TopHat.Relevance;
using TopHat.Relevance.BM25;
using TopHat.Transforms.JsonContext;
using TopHat.Transforms.JsonContext.Strategies;
using Xunit;

namespace TopHat.Tests.Transforms.JsonContext;

public sealed class NumberArrayCompressionStrategyTests
{
	private static JsonCompressionContext MakeCtx(string queryContext = "") =>
		new()
		{
			QueryContext = queryContext,
			Scorer = new BM25Scorer(),
			Options = new JsonContextCompressorOptions { MaxItemsAfterCrush = 15 },
		};

	private static JsonNode?[] MakeNumbers(IEnumerable<double> values) =>
		values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray();

	[Fact]
	public void Compress_SmallArray_NoModification()
	{
		var items = MakeNumbers([1, 2, 3, 4]);
		var (kept, modified) = NumberArrayCompressionStrategy.Compress(items, MakeCtx());
		Assert.False(modified);
		Assert.Equal(4, kept.Count);
	}

	[Fact]
	public void Compress_LargeUniformArray_ReducesCount()
	{
		// 100 identical values — should compress well.
		var items = MakeNumbers(Enumerable.Repeat(42.0, 100));
		var (kept, modified) = NumberArrayCompressionStrategy.Compress(items, MakeCtx());
		Assert.True(modified);
		Assert.True(kept.Count < 100);
	}

	[Fact]
	public void Compress_PreservesFirstAndLastItems()
	{
		var values = Enumerable.Repeat(5.0, 80).ToList();
		var items = MakeNumbers(values);
		var (kept, _) = NumberArrayCompressionStrategy.Compress(items, MakeCtx());

		// The very first and very last numeric values should be in the result.
		var keptValues = kept.Select(n => n!.GetValue<double>()).ToArray();
		Assert.Contains(values[0], keptValues);
		Assert.Contains(values[^1], keptValues);
	}

	[Fact]
	public void Compress_PreservesOutliers()
	{
		// 79 values at 1.0, one extreme outlier at 1000.0.
		var values = Enumerable.Repeat(1.0, 79).Append(1000.0).ToList();
		var items = MakeNumbers(values);
		var (kept, modified) = NumberArrayCompressionStrategy.Compress(items, MakeCtx());

		Assert.True(modified);
		var keptValues = kept.Select(n => n!.GetValue<double>()).ToArray();
		Assert.Contains(1000.0, keptValues);
	}

	[Fact]
	public void Compress_PreservesChangePoints()
	{
		// 20 values of 1.0 then 10 values of 1000.0 — asymmetric step.
		var values = Enumerable.Range(0, 30)
			.Select(i => i < 20 ? 1.0 : 1000.0)
			.ToList();
		var items = MakeNumbers(values);
		var ctx = new JsonCompressionContext
		{
			Scorer = new BM25Scorer(),
			Options = new JsonContextCompressorOptions { MaxItemsAfterCrush = 15, PreserveChangePoints = true },
		};
		var (kept, modified) = NumberArrayCompressionStrategy.Compress(items, ctx);

		// At least one value from each segment should be present.
		var keptValues = kept.Select(n => n!.GetValue<double>()).ToArray();
		Assert.Contains(1.0, keptValues);
		Assert.Contains(1000.0, keptValues);
	}

	[Fact]
	public void Compress_ResultItemsAreInOriginalOrder()
	{
		var values = Enumerable.Range(1, 50).Select(i => (double)i).ToList();
		var items = MakeNumbers(values);
		var (kept, _) = NumberArrayCompressionStrategy.Compress(items, MakeCtx());

		var keptValues = kept.Select(n => n!.GetValue<double>()).ToArray();
		Assert.Equal(keptValues, keptValues.OrderBy(v => v).ToArray());
	}
}
