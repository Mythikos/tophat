using TopHat.Feedback;
using Xunit;

namespace TopHat.Tests.Feedback;

public sealed class FeedbackDecisionTests
{
	private static FeedbackThresholds Defaults() => new();

	private static CompressionFeedbackStats Stats(
		long compressions = 0,
		long retrievals = 0,
		long full = 0,
		long search = 0,
		long budget = 0,
		FeedbackOverride manualOverride = FeedbackOverride.None) =>
		new("t", compressions, retrievals, full, search, budget, manualOverride, DateTimeOffset.UtcNow);

	[Fact]
	public void NullStats_ReturnsStandard()
	{
		var guidance = FeedbackDecision.Decide(stats: null, Defaults());
		Assert.False(guidance.SkipCompression);
	}

	[Fact]
	public void ManualOverrideSkip_AlwaysSkips()
	{
		var stats = Stats(compressions: 1, manualOverride: FeedbackOverride.SkipCompression);
		var guidance = FeedbackDecision.Decide(stats, Defaults());
		Assert.True(guidance.SkipCompression);
	}

	[Fact]
	public void ManualOverrideAlwaysCompress_OverridesEvenHighRetrieval()
	{
		// High retrieval rate would normally trigger skip; AlwaysCompress overrides.
		var stats = Stats(compressions: 100, retrievals: 100, full: 100, manualOverride: FeedbackOverride.AlwaysCompress);
		var guidance = FeedbackDecision.Decide(stats, Defaults());
		Assert.False(guidance.SkipCompression);
	}

	[Fact]
	public void ColdStart_ReturnsStandard()
	{
		// Below MinSamplesForHints (5 default) → standard regardless of high rates.
		var stats = Stats(compressions: 3, retrievals: 3, full: 3);
		var guidance = FeedbackDecision.Decide(stats, Defaults());
		Assert.False(guidance.SkipCompression);
		Assert.Contains("cold start", guidance.Reason);
	}

	[Fact]
	public void HighRetrievalAndHighFull_SkipsCompression()
	{
		// 80% retrieval rate, 90% full → both thresholds exceeded.
		var stats = Stats(compressions: 10, retrievals: 8, full: 7, search: 1);
		var guidance = FeedbackDecision.Decide(stats, Defaults());
		Assert.True(guidance.SkipCompression);
	}

	[Fact]
	public void HighRetrievalLowFull_DoesNotSkip()
	{
		// 70% retrieval rate, only 30% full → search-dominant, compression still useful.
		var stats = Stats(compressions: 10, retrievals: 7, full: 2, search: 5);
		var guidance = FeedbackDecision.Decide(stats, Defaults());
		Assert.False(guidance.SkipCompression);
	}

	[Fact]
	public void LowRetrieval_DoesNotSkip()
	{
		// 20% retrieval rate, even if all are full → low rate means tool is mostly fine compressed.
		var stats = Stats(compressions: 10, retrievals: 2, full: 2);
		var guidance = FeedbackDecision.Decide(stats, Defaults());
		Assert.False(guidance.SkipCompression);
	}

	[Fact]
	public void BudgetExhausted_FoldsIntoFullForDecision()
	{
		// 90% retrieval rate, 0 full retrievals but 9 budget-exhausted = full+budget = 9/9 = 100%
		// → should still skip because budget-exhausted is "tool needed everything but got cut off."
		var stats = Stats(compressions: 10, retrievals: 9, full: 0, budget: 9);
		var guidance = FeedbackDecision.Decide(stats, Defaults());
		Assert.True(guidance.SkipCompression);
	}

	[Fact]
	public void CustomThresholds_AreRespected()
	{
		// Make thresholds stricter; same stats should not trigger skip.
		var thresholds = new FeedbackThresholds
		{
			MinSamplesForHints = 5,
			HighRetrievalThreshold = 0.9,    // bump from 0.5
			FullRetrievalThreshold = 0.95,   // bump from 0.8
		};
		var stats = Stats(compressions: 10, retrievals: 8, full: 7); // would skip with defaults
		var guidance = FeedbackDecision.Decide(stats, thresholds);
		Assert.False(guidance.SkipCompression);
	}
}
