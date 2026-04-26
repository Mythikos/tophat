using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TopHat.Relevance.Onnx;
using TopHat.Relevance.Onnx.DependencyInjection;
using Xunit;

namespace TopHat.Tests.Relevance.Onnx;

/// <summary>
/// End-to-end sanity checks that exercise real ONNX inference — tokenization, session run,
/// pooling, and cosine scoring together. Skipped automatically when the MiniLM-L6-v2 model
/// is not present on disk, so CI without the model still passes.
/// </summary>
public sealed class OnnxScorerIntegrationTests
{
	// Canonical location used during TopHat.Relevance.Onnx development.
	// Override at runtime with TOPHAT_ONNX_MODEL_DIR if the model lives elsewhere.
	private const string DefaultModelDirectory = @"D:\Repository\Visual Studio\ContextOptimize\TopHat\Resources\Models\AllMiniLML6V2";

	private static string? LocateModelDirectory()
	{
		var envOverride = Environment.GetEnvironmentVariable("TOPHAT_ONNX_MODEL_DIR");
		var candidate = string.IsNullOrWhiteSpace(envOverride) ? DefaultModelDirectory : envOverride;

		if (!File.Exists(Path.Combine(candidate, "model.onnx")))
		{
			return null;
		}

		if (!File.Exists(Path.Combine(candidate, "vocab.txt")))
		{
			return null;
		}

		return candidate;
	}

	[Fact]
	public void ScoreBatch_SemanticallyCloseItem_ScoresHigherThanUnrelatedItem()
	{
		var dir = LocateModelDirectory();

		if (dir is null)
		{
			return;
		}

		using var provider = BuildProvider(dir);
		var scorer = provider.GetRequiredService<OnnxScorer>();

		const string context = "The user is trying to debug a failed database connection.";
		var items = new[]
		{
			"error: could not connect to postgres server on port 5432",
			"the weather in paris is lovely this time of year",
		};

		var scores = scorer.ScoreBatch(items, context);

		Assert.Equal(2, scores.Count);
		Assert.All(scores, s => Assert.InRange(s.Score, 0.0, 1.0));
		Assert.True(
			scores[0].Score > scores[1].Score,
			$"Expected DB-error item to outscore weather item. Got: {scores[0].Score:F3} vs {scores[1].Score:F3}");
		Assert.StartsWith("ONNX cosine:", scores[0].Reason, StringComparison.Ordinal);
	}

	[Fact]
	public void ScoreBatch_ParaphrasedItem_ScoresHigh()
	{
		// The paraphrase fixture — the whole reason we're adding ONNX. BM25 would score this ~0
		// because no surface terms overlap (aside from stopwords).
		var dir = LocateModelDirectory();

		if (dir is null)
		{
			return;
		}

		using var provider = BuildProvider(dir);
		var scorer = provider.GetRequiredService<OnnxScorer>();

		const string context = "customer payments are overdue";
		var items = new[]
		{
			"invoice past the due date, balance unpaid",
			"new marketing campaign launches next Tuesday",
		};

		var scores = scorer.ScoreBatch(items, context);

		Assert.True(
			scores[0].Score > 0.4,
			$"Expected paraphrase to score > 0.4, got {scores[0].Score:F3}");
		Assert.True(
			scores[0].Score > scores[1].Score + 0.1,
			$"Expected paraphrase to outscore unrelated item by > 0.1. Got: {scores[0].Score:F3} vs {scores[1].Score:F3}");
	}

	[Fact]
	public void ScoreBatch_LargeBatch_StaysWithinBatchSize()
	{
		// Sanity check the multi-batch path — 100 docs with BatchSize=16 forces 7 session runs.
		var dir = LocateModelDirectory();

		if (dir is null)
		{
			return;
		}

		var services = new ServiceCollection();
		services.AddTopHatOnnxRelevance(OnnxRelevanceModels.AllMiniLML6V2(dir), opts => opts.BatchSize = 16);
		using var provider = services.BuildServiceProvider();
		var scorer = provider.GetRequiredService<OnnxScorer>();

		var items = Enumerable.Range(0, 100).Select(i => $"document number {i} with some filler content").ToArray();
		var scores = scorer.ScoreBatch(items, "find document 42");

		Assert.Equal(100, scores.Count);
		Assert.All(scores, s => Assert.InRange(s.Score, 0.0, 1.0));
	}

	[Fact]
	public void ScoreBatch_EmptyContext_ReturnsZeroScores()
	{
		var dir = LocateModelDirectory();

		if (dir is null)
		{
			return;
		}

		using var provider = BuildProvider(dir);
		var scorer = provider.GetRequiredService<OnnxScorer>();

		var scores = scorer.ScoreBatch(new[] { "anything" }, context: "");

		Assert.Single(scores);
		Assert.Equal(0.0, scores[0].Score);
	}

	private static ServiceProvider BuildProvider(string modelDirectory)
	{
		var services = new ServiceCollection();
		services.AddTopHatOnnxRelevance(OnnxRelevanceModels.AllMiniLML6V2(modelDirectory));
		return services.BuildServiceProvider();
	}
}
