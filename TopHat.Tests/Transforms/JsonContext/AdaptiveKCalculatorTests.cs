using TopHat.Transforms.JsonContext.Common;
using Xunit;

namespace TopHat.Tests.Transforms.JsonContext;

public sealed class AdaptiveKCalculatorTests
{
	#region Tier 1: trivial fast path

	[Fact]
	public void Compute_FewItems_ReturnsAll()
	{
		var items = Enumerable.Range(1, 8).Select(i => $"item {i}").ToList();
		Assert.Equal(8, AdaptiveKCalculator.Compute(items));
	}

	[Fact]
	public void Compute_Empty_ReturnsZero()
	{
		Assert.Equal(0, AdaptiveKCalculator.Compute([]));
	}

	#endregion

	#region Tier 2: Kneedle on bigram curve

	[Fact]
	public void Compute_AllDuplicates_ReturnsNearMinK()
	{
		// 50 identical items — SimHash near-total redundancy path.
		var items = Enumerable.Repeat("exactly the same content for every single item here", 50).ToList();
		var k = AdaptiveKCalculator.Compute(items, minK: 3);
		Assert.True(k <= 10, $"Expected small k for near-duplicate items, got {k}");
		Assert.True(k >= 3);
	}

	[Fact]
	public void Compute_AllUnique_ReturnsHighK()
	{
		// 50 completely unique items with distinct UUIDs and content.
		var items = Enumerable.Range(1, 50)
			.Select(i => $"unique item {i} has guid {Guid.NewGuid()} and different words: alpha{i} beta{i} gamma{i}")
			.ToList();
		var k = AdaptiveKCalculator.Compute(items, minK: 3);
		// High diversity → should keep a significant fraction.
		Assert.True(k >= 20, $"Expected high k for unique items, got {k}");
	}

	[Fact]
	public void Compute_RespectsMinK()
	{
		var items = Enumerable.Repeat("repeated content word word word word word", 100).ToList();
		var k = AdaptiveKCalculator.Compute(items, minK: 5);
		Assert.True(k >= 5);
	}

	[Fact]
	public void Compute_RespectsMaxK()
	{
		var items = Enumerable.Range(1, 50)
			.Select(i => $"unique item {i}: {Guid.NewGuid()} data field value result output")
			.ToList();
		var k = AdaptiveKCalculator.Compute(items, maxK: 10);
		Assert.True(k <= 10, $"Expected k ≤ 10, got {k}");
	}

	#endregion

	#region FindKnee / bigram curve internals

	[Fact]
	public void FindKnee_FlatCurve_ReturnsOne()
	{
		var curve = Enumerable.Repeat(10, 20).ToList();
		var knee = AdaptiveKCalculator.FindKnee(curve);
		Assert.Equal(1, knee);
	}

	[Fact]
	public void FindKnee_TooShort_ReturnsNull()
	{
		Assert.Null(AdaptiveKCalculator.FindKnee([1, 2]));
	}

	#endregion
}
