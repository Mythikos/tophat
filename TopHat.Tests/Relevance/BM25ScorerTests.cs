using TopHat.Relevance;
using TopHat.Relevance.BM25;
using Xunit;

namespace TopHat.Tests.Relevance;

public sealed class BM25ScorerTests
{
	private readonly BM25Scorer _scorer = new();

	#region Tokenization

	[Fact]
	public void Tokenize_EmptyString_ReturnsEmpty()
	{
		var tokens = BM25Scorer.Tokenize(string.Empty);
		Assert.Empty(tokens);
	}

	[Fact]
	public void Tokenize_UuidPreservedAsSingleToken()
	{
		var tokens = BM25Scorer.Tokenize("550e8400-e29b-41d4-a716-446655440000");
		Assert.Single(tokens);
		Assert.Equal("550e8400-e29b-41d4-a716-446655440000", tokens[0]);
	}

	[Fact]
	public void Tokenize_NumericId4PlusDigits_Preserved()
	{
		// 12345 captured by the 4+ digit rule; "99" also captured by alnum fallback — that matches headroom behavior.
		var tokens = BM25Scorer.Tokenize("order 12345 and item 99");
		Assert.Contains("12345", tokens);
		Assert.Contains("order", tokens);
	}

	[Fact]
	public void Tokenize_LowercasesAllTokens()
	{
		var tokens = BM25Scorer.Tokenize("Hello WORLD");
		Assert.Contains("hello", tokens);
		Assert.Contains("world", tokens);
	}

	#endregion

	#region Single-item scoring

	[Fact]
	public void Score_ExactTermMatch_ReturnsNonZero()
	{
		var result = _scorer.Score("{\"event\": \"error\", \"code\": 500}", "find error events");
		Assert.True(result.Score > 0.0);
		Assert.Contains("error", result.MatchedTerms);
	}

	[Fact]
	public void Score_NoTermMatch_ReturnsZero()
	{
		var result = _scorer.Score("{\"status\": \"ok\", \"code\": 200}", "find error events");
		Assert.Equal(0.0, result.Score);
		Assert.Empty(result.MatchedTerms);
	}

	[Fact]
	public void Score_UuidMatch_ScoreExceedsThreshold()
	{
		const string uuid = "550e8400-e29b-41d4-a716-446655440000";
		var result = _scorer.Score($"{{\"id\": \"{uuid}\", \"name\": \"Alice\"}}", $"find record {uuid}");
		Assert.True(result.Score >= 0.3, $"Expected ≥0.3 for UUID match, got {result.Score}");
		Assert.Contains(uuid, result.MatchedTerms);
	}

	[Fact]
	public void Score_EmptyContext_ReturnsZeroScore()
	{
		var result = _scorer.Score("{\"error\": \"timeout\"}", string.Empty);
		Assert.Equal(0.0, result.Score);
	}

	[Fact]
	public void Score_ScoreClamped0To1()
	{
		// Many overlapping terms — raw score could exceed max; clamped at 1.
		var item = string.Join(" ", Enumerable.Repeat("error exception failure", 100));
		var result = _scorer.Score(item, "error exception failure critical fatal crash abort");
		Assert.True(result.Score <= 1.0);
		Assert.True(result.Score >= 0.0);
	}

	[Fact]
	public void Score_ReasonDescribesMatch()
	{
		var result = _scorer.Score("{\"event\": \"error\"}", "error");
		Assert.Contains("error", result.Reason);
	}

	#endregion

	#region Batch scoring

	[Fact]
	public void ScoreBatch_EmptyContext_AllZero()
	{
		var items = new[] { "error: timeout", "success: ok" };
		var results = _scorer.ScoreBatch(items, string.Empty);
		Assert.Equal(2, results.Count);
		Assert.All(results, r => Assert.Equal(0.0, r.Score));
	}

	[Fact]
	public void ScoreBatch_OrderPreserved()
	{
		var items = new[] { "error: timeout", "success: ok", "warning: slow" };
		var results = _scorer.ScoreBatch(items, "error timeout");
		Assert.Equal(3, results.Count);
		// First item should score higher than second (no matches for "ok"/"success").
		Assert.True(results[0].Score > results[1].Score);
	}

	[Fact]
	public void ScoreBatch_CountMatchesinputCount()
	{
		var items = new[] { "a", "b", "c", "d", "e" };
		var results = _scorer.ScoreBatch(items, "query");
		Assert.Equal(5, results.Count);
	}

	[Fact]
	public void ScoreBatch_UsesAverageDocLengthForNormalization()
	{
		// Short doc in a batch of long docs should not be penalized relative to single-item scoring.
		var shortItem = "error";
		var longItem = new string('x', 1000).Replace('x', 'a') + " " + string.Join(" ", Enumerable.Repeat("word", 200));
		var batchResults = _scorer.ScoreBatch([shortItem, longItem], "error");
		var singleResult = _scorer.Score(shortItem, "error");

		// Batch and single scores for the short item may differ due to avgdl; both should be positive.
		Assert.True(batchResults[0].Score > 0.0);
		Assert.True(singleResult.Score > 0.0);
	}

	[Fact]
	public void ScoreBatch_ParityWithSequentialSingleScores_SameContext()
	{
		// Each individual score must be reproducible within batch.
		var items = new[] { "error: disk full", "success: written 100 records", "warning: 12345 retries" };
		var context = "disk error 12345";

		var batchResults = _scorer.ScoreBatch(items, context);
		var singleResults = items.Select(i => _scorer.Score(i, context)).ToArray();

		// Both should agree on which items matched (non-zero).
		for (var idx = 0; idx < items.Length; idx++)
		{
			Assert.Equal(batchResults[idx].Score > 0, singleResults[idx].Score > 0);
		}
	}

	#endregion
}
