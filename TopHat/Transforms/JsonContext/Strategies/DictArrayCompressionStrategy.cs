using System.Text.Json.Nodes;
using TopHat.Compression.CCR;
using TopHat.Transforms.JsonContext.Common;
using TopHat.Transforms.JsonContext.Summarization;

namespace TopHat.Transforms.JsonContext.Strategies;

/// <summary>
/// Compresses a JSON array of objects (dicts) using adaptive K + preservation guarantees + relevance scoring.
/// Port of headroom's SmartCrusher._crush_array (simplified — no TOIN, no CCR).
/// </summary>
internal static class DictArrayCompressionStrategy
{
	/// <summary>
	/// Metadata key used to mark a synthetic object appended to the kept array describing what was
	/// dropped. Prefixed with <c>_tophat_</c> so the LLM (and any downstream parser) can easily
	/// distinguish it from real items.
	/// </summary>
	internal const string CompressionMetadataKey = "_tophat_compression";

	/// <summary>
	/// Compresses <paramref name="items"/> and returns a subset that preserves error-bearing items,
	/// boundary items, and the most relevant items per the query context. When one or more
	/// <see cref="IDroppedItemsSummarizer"/>s are registered in the context, a trailing metadata
	/// object is appended to describe the dropped items.
	/// </summary>
	public static (IReadOnlyList<JsonNode?> Kept, bool WasModified) Compress(
		IReadOnlyList<JsonNode?> items,
		JsonCompressionContext ctx)
	{
		var n = items.Count;

		if (n < ctx.Options.MinItemsToAnalyze)
		{
			return (items, false);
		}

		var itemStrings = items.Select(node => node?.ToJsonString() ?? "null").ToArray();
		var kTotal = AdaptiveKCalculator.Compute(itemStrings, minK: 3, maxK: ctx.Options.MaxItemsAfterCrush);

		if (kTotal >= n)
		{
			return (items, false);
		}

		var kFirst = Math.Max(1, (int)Math.Round(kTotal * ctx.Options.FirstFraction));
		var kLast = Math.Max(1, (int)Math.Round(kTotal * ctx.Options.LastFraction));
		var kImportance = Math.Max(0, kTotal - kFirst - kLast);

		var keepIndices = new HashSet<int>();

		// Boundary: first-K. Preserves "what's this a list of?" schema context.
		for (var idx = 0; idx < Math.Min(kFirst, n); idx++)
		{
			keepIndices.Add(idx);
		}

		// Boundary: last-K. Preserves "what was the latest entry?" context.
		for (var idx = Math.Max(0, n - kLast); idx < n; idx++)
		{
			keepIndices.Add(idx);
		}

		// Mandatory: error-keyword-bearing items.
		for (var idx = 0; idx < n; idx++)
		{
			if (ErrorKeywordDetector.ContainsErrorKeyword(itemStrings[idx]))
			{
				keepIndices.Add(idx);
			}
		}

		// Mandatory: items containing literal high-signal tokens from the query (quoted strings,
		// camelCase/snake_case identifiers, paths). BM25's length normalization can otherwise rank
		// a slightly longer snippet below boundary-filler items even when it contains the exact
		// query term. A term matching too many items (e.g., a common field name) is skipped.
		if (!string.IsNullOrEmpty(ctx.QueryContext))
		{
			var queryTerms = QueryTermDetector.ExtractTerms(ctx.QueryContext);

			if (queryTerms.Length > 0)
			{
				var queryPreserved = QueryTermDetector.FindPreservedIndices(itemStrings, queryTerms, maxMatchesPerTerm: kTotal);

				foreach (var idx in queryPreserved)
				{
					keepIndices.Add(idx);
				}
			}
		}

		// Importance: relevance-top-K (fill remaining budget from relevance scores). Candidates
		// exclude already-reserved items so the scorer gets the full unused budget.
		if (kImportance > 0 && !string.IsNullOrEmpty(ctx.QueryContext))
		{
			var candidateIndices = Enumerable.Range(0, n)
				.Where(idx => !keepIndices.Contains(idx))
				.ToArray();

			if (candidateIndices.Length > 0)
			{
				var candidateStrings = candidateIndices.Select(idx => itemStrings[idx]).ToArray();
				var scores = ctx.Scorer.ScoreBatch(candidateStrings, ctx.QueryContext);

				var topK = candidateIndices
					.Zip(scores, (idx, score) => (Index: idx, Score: score.Score))
					.OrderByDescending(t => t.Score)
					.ThenBy(t => t.Index)
					.Take(kImportance);

				foreach (var (idx, _) in topK)
				{
					keepIndices.Add(idx);
				}
			}
		}
		else if (kImportance > 0)
		{
			// No query context — fill with evenly-spaced stride samples.
			var stride = Math.Max(1, (n - 1) / (kImportance + 1));

			for (var strideIdx = stride; strideIdx < n && keepIndices.Count < kTotal; strideIdx += stride)
			{
				keepIndices.Add(strideIdx);
			}
		}

		var keptOrdered = keepIndices.OrderBy(i => i).ToArray();
		var wasModified = keptOrdered.Length < n;
		var kept = keptOrdered.Select(i => items[i]).ToList();

		if (wasModified)
		{
			var droppedObjects = CollectDroppedObjects(items, keepIndices);
			var summaryNode = BuildSummaryNode(droppedObjects, originalCount: n, keptCount: keptOrdered.Length, ctx);

			if (summaryNode is not null)
			{
				kept.Add(summaryNode);
			}
		}

		return (kept, wasModified);
	}

	private static List<JsonObject> CollectDroppedObjects(IReadOnlyList<JsonNode?> items, HashSet<int> keepIndices)
	{
		var dropped = new List<JsonObject>(items.Count - keepIndices.Count);

		for (var idx = 0; idx < items.Count; idx++)
		{
			if (keepIndices.Contains(idx))
			{
				continue;
			}

			if (items[idx] is JsonObject obj)
			{
				dropped.Add(obj);
			}
		}

		return dropped;
	}

	private static JsonObject? BuildSummaryNode(
		List<JsonObject> dropped,
		int originalCount,
		int keptCount,
		JsonCompressionContext ctx)
	{
		if (dropped.Count == 0)
		{
			return null;
		}

		var fragments = new List<string>(ctx.Summarizers.Count);

		if (ctx.Summarizers.Count > 0)
		{
			var summarizationContext = new SummarizationContext
			{
				OriginalCount = originalCount,
				KeptCount = keptCount,
			};

			foreach (var summarizer in ctx.Summarizers)
			{
				var fragment = summarizer.Summarize(dropped, summarizationContext);

				if (!string.IsNullOrWhiteSpace(fragment))
				{
					fragments.Add(fragment);
				}
			}
		}

		// Register dropped items for CCR retrieval, if the transform wired up a callback. Returns
		// a retrieval key (GUID) to surface in the metadata so the model can echo it back when
		// calling the tophat_retrieve tool.
		string? retrievalKey = null;

		if (ctx.RegisterDroppedItemsForRetrieval is not null)
		{
			retrievalKey = ctx.RegisterDroppedItemsForRetrieval(dropped);
		}

		// If nothing interesting to emit (no summary fragments and no retrieval key), skip metadata
		// entirely so the kept array isn't polluted with an empty marker object.
		if (fragments.Count == 0 && string.IsNullOrEmpty(retrievalKey))
		{
			return null;
		}

		var metadata = new JsonObject
		{
			["omitted"] = originalCount - keptCount,
			["kept"] = keptCount,
		};

		if (fragments.Count > 0)
		{
			metadata["summary"] = string.Join("; ", fragments);
		}

		if (!string.IsNullOrEmpty(retrievalKey))
		{
			metadata[CCRToolDefinition.RetrievalKeyField] = retrievalKey;
		}

		return new JsonObject { [CompressionMetadataKey] = metadata };
	}
}
