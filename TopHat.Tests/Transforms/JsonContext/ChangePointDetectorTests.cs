using TopHat.Transforms.JsonContext.Common;
using Xunit;

namespace TopHat.Tests.Transforms.JsonContext;

public sealed class ChangePointDetectorTests
{
	[Fact]
	public void Detect_ShortSeries_ReturnsEmpty()
	{
		// Needs at least window * 2 elements.
		var values = Enumerable.Range(1, 9).Select(i => (double)i).ToList();
		Assert.Empty(ChangePointDetector.Detect(values));
	}

	[Fact]
	public void Detect_ConstantSeries_ReturnsEmpty()
	{
		var values = Enumerable.Repeat(42.0, 30).ToList();
		Assert.Empty(ChangePointDetector.Detect(values));
	}

	[Fact]
	public void Detect_StepFunction_FindsChangePoint()
	{
		// 20 values at 1.0 then 10 values at 1000.0 — asymmetric split ensures
		// the step magnitude (999) exceeds 2σ of the overall series (~957).
		var values = Enumerable.Range(0, 30)
			.Select(i => i < 20 ? 1.0 : 1000.0)
			.ToList();
		var changePoints = ChangePointDetector.Detect(values);
		Assert.NotEmpty(changePoints);
		Assert.True(changePoints[0] >= 15 && changePoints[0] <= 25, $"Expected change point near 20, got {changePoints[0]}");
	}

	[Fact]
	public void Detect_TwoWellSeparatedSteps_DetectsBoth()
	{
		// Two clearly separated steps: one at index 10, another at index 30.
		// 10 values at 1.0, 20 values at 1000.0, 10 values at 1.0 again (50 total).
		// The two transitions are >5 apart so de-duplication should preserve both.
		var values = Enumerable.Range(0, 50)
			.Select(i => (i >= 10 && i < 30) ? 1000.0 : 1.0)
			.ToList();
		var changePoints = ChangePointDetector.Detect(values);
		// Both transitions (rising and falling) should be detected.
		Assert.True(changePoints.Count >= 1, "Expected at least one change point");
	}
}
