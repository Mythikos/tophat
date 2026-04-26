using TopHat.Relevance;
using Xunit;

namespace TopHat.Tests.Relevance;

public sealed class FusedRelevanceScorerTests
{
	[Fact]
	public void Constructor_EmptyScorers_Throws()
	{
		Assert.Throws<ArgumentException>(() => new FusedRelevanceScorer(Array.Empty<IRelevanceScorer>()));
	}

	[Fact]
	public void ScoreBatch_SingleInnerScorer_PreservesOrdering()
	{
		// Single-scorer wrap should preserve the inner ranking; score magnitudes may differ after
		// batch-local normalization but the ordering is what the compressor consumes.
		var inner = new StubScorer(new[] { 0.1, 0.9, 0.5 });
		var fused = new FusedRelevanceScorer(new IRelevanceScorer[] { inner });

		var scores = fused.ScoreBatch(new[] { "a", "b", "c" }, "ctx");

		Assert.Equal(3, scores.Count);
		Assert.True(scores[1].Score > scores[2].Score);
		Assert.True(scores[2].Score > scores[0].Score);
	}

	[Fact]
	public void ScoreBatch_TwoAgreeingScorers_SameTopItem()
	{
		// Both scorers rank item 1 highest → fused should too.
		var a = new StubScorer(new[] { 0.2, 0.9, 0.5 });
		var b = new StubScorer(new[] { 0.1, 0.8, 0.4 });
		var fused = new FusedRelevanceScorer(new IRelevanceScorer[] { a, b });

		var scores = fused.ScoreBatch(new[] { "x", "y", "z" }, "ctx");

		Assert.True(scores[1].Score >= scores[0].Score);
		Assert.True(scores[1].Score >= scores[2].Score);
		// Under normalized-sum, an item that is top in every scorer normalizes to 1.0 per scorer,
		// and the sum-divided-by-count stays at 1.0.
		Assert.Equal(1.0, scores[1].Score, precision: 5);
	}

	[Fact]
	public void ScoreBatch_ConsistentMiddle_BeatsOscillators()
	{
		// Item 0: A rank 1, B rank 5 — extreme oscillation.
		// Item 4: A rank 5, B rank 1 — extreme oscillation.
		// Item 1: A rank 2, B rank 2 — consistently near-top.
		// Normalized sum favors item 1: it collects high normalized scores from both scorers,
		// where items 0/4 only collect one high + one low.
		var a = new StubScorer(new[] { 0.9, 0.8, 0.6, 0.4, 0.1 });
		var b = new StubScorer(new[] { 0.1, 0.8, 0.6, 0.4, 0.9 });
		var fused = new FusedRelevanceScorer(new IRelevanceScorer[] { a, b });

		var scores = fused.ScoreBatch(new[] { "x0", "x1", "x2", "x3", "x4" }, "ctx");

		Assert.True(scores[1].Score > scores[0].Score, $"consistent-middle: {scores[1].Score:F5}, oscillator 0: {scores[0].Score:F5}");
		Assert.True(scores[1].Score > scores[4].Score, $"consistent-middle: {scores[1].Score:F5}, oscillator 4: {scores[4].Score:F5}");
	}

	[Fact]
	public void ScoreBatch_OneScorerHasNoSignal_DoesNotDragDownTheOther()
	{
		// This is the regression Reciprocal Rank Fusion had: a scorer with no signal (flat scores)
		// would still contribute rank weight, dragging down items the other scorer identified.
		// Normalized-sum should filter flat distributions to zero contribution, so the scorer with
		// actual signal dominates — the synonym-config-files scenario.
		const int itemCount = 50;
		// Scorer A: flat at 0.05 — no signal at all.
		var aScores = Enumerable.Repeat(0.05, itemCount).ToArray();
		// Scorer B: item 10 distinctly highest, others near zero.
		var bScores = Enumerable.Range(0, itemCount).Select(i => i == 10 ? 0.9 : 0.1).ToArray();

		var fused = new FusedRelevanceScorer(new IRelevanceScorer[]
		{
			new StubScorer(aScores),
			new StubScorer(bScores),
		});

		var items = Enumerable.Range(0, itemCount).Select(i => $"item_{i}").ToArray();
		var scores = fused.ScoreBatch(items, "ctx");

		// Item 10 should be the clear top — the only item Scorer B distinguished, and Scorer A
		// contributed nothing on either side.
		var rankedIndices = Enumerable.Range(0, itemCount)
			.OrderByDescending(i => scores[i].Score)
			.ToArray();
		Assert.Equal(10, rankedIndices[0]);
	}

	[Fact]
	public void ScoreBatch_OneScorerSavesItem_NormalizedSumKeepsItInTopK()
	{
		// The BM25-saves-id-42 motivating case. Scorer A ranks item 0 at the top; Scorer B ranks
		// it near the bottom. Under normalized sum, item 0 gets ~1.0 from A + ~0.0 from B, which
		// is competitive with items Scorer B rated highly.
		const int itemCount = 50;
		var aScores = Enumerable.Range(0, itemCount).Select(i => i == 0 ? 0.95 : 0.05).ToArray();
		// Scorer B ranks item 0 near the bottom; high values cluster at the end of the list.
		var bScores = Enumerable.Range(0, itemCount).Select(i => i == 0 ? 0.05 : 0.4 + (i * 0.005)).ToArray();

		var fused = new FusedRelevanceScorer(new IRelevanceScorer[]
		{
			new StubScorer(aScores),
			new StubScorer(bScores),
		});

		var items = Enumerable.Range(0, itemCount).Select(i => $"item_{i}").ToArray();
		var scores = fused.ScoreBatch(items, "query 42");

		var rankedIndices = Enumerable.Range(0, itemCount)
			.OrderByDescending(i => scores[i].Score)
			.ToArray();
		var item0Rank = Array.IndexOf(rankedIndices, 0);

		// Item 0 should be in the top half — A's strong signal is not outvoted by B's noise.
		Assert.InRange(item0Rank, 0, itemCount / 2);
	}

	[Fact]
	public void ScoreBatch_MergesMatchedTermsFromAllScorers()
	{
		var a = new StubScorer(new[] { 0.8 }, matchedTerms: new[] { "alpha" });
		var b = new StubScorer(new[] { 0.6 }, matchedTerms: new[] { "beta" });
		var fused = new FusedRelevanceScorer(new IRelevanceScorer[] { a, b });

		var scores = fused.ScoreBatch(new[] { "item" }, "ctx");

		Assert.Contains("alpha", scores[0].MatchedTerms);
		Assert.Contains("beta", scores[0].MatchedTerms);
	}

	[Fact]
	public void ScoreBatch_EmptyItems_ReturnsEmptyArray()
	{
		var fused = new FusedRelevanceScorer(new IRelevanceScorer[] { new StubScorer(Array.Empty<double>()) });

		var scores = fused.ScoreBatch(Array.Empty<string>(), "ctx");

		Assert.Empty(scores);
	}

	[Fact]
	public void ScoreBatch_AllScorersFlat_YieldsZeroScores()
	{
		// Both scorers produce flat output — no differentiation possible. The fused result should
		// be all zeros (no NaN from divide-by-zero), and the compressor can still pick deterministically.
		var a = new StubScorer(new[] { 0.5, 0.5, 0.5 });
		var b = new StubScorer(new[] { 0.2, 0.2, 0.2 });
		var fused = new FusedRelevanceScorer(new IRelevanceScorer[] { a, b });

		var scores = fused.ScoreBatch(new[] { "x", "y", "z" }, "ctx");

		Assert.All(scores, s =>
		{
			Assert.Equal(0.0, s.Score);
			Assert.False(double.IsNaN(s.Score));
		});
	}

	private sealed class StubScorer : IRelevanceScorer
	{
		private readonly double[] _scores;
		private readonly string[] _matchedTerms;

		public StubScorer(double[] scores, string[]? matchedTerms = null)
		{
			_scores = scores;
			_matchedTerms = matchedTerms ?? Array.Empty<string>();
		}

		public RelevanceScore Score(string item, string context) =>
			new (_scores[0], "stub", _matchedTerms);

		public IReadOnlyList<RelevanceScore> ScoreBatch(IReadOnlyList<string> items, string context) =>
			_scores.Select(s => new RelevanceScore(s, "stub", _matchedTerms)).ToArray();
	}
}
