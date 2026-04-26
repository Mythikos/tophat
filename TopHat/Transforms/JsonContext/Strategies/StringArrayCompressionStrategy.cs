using System.Text.Json.Nodes;
using TopHat.Transforms.JsonContext.Common;

namespace TopHat.Transforms.JsonContext.Strategies;

/// <summary>
/// Compresses a JSON array of strings using dedup + adaptive sampling + error preservation.
/// Port of headroom's SmartCrusher._crush_string_array.
/// </summary>
internal static class StringArrayCompressionStrategy
{
	/// <summary>
	/// Compresses <paramref name="items"/> and returns a deduplicated, sampled subset.
	/// Preserves error-bearing strings and anomalously-long strings unconditionally.
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

		var strings = items.Select(node =>
		{
			if (node is JsonValue jv && jv.TryGetValue<string>(out var s))
			{
				return s;
			}

			return string.Empty;
		}).ToArray();

		var kTotal = AdaptiveKCalculator.Compute(strings, minK: 3, maxK: ctx.Options.MaxItemsAfterCrush);

		if (kTotal >= n)
		{
			return (items, false);
		}

		var kFirst = Math.Max(1, (int)Math.Round(kTotal * ctx.Options.FirstFraction));
		var kLast = Math.Max(1, (int)Math.Round(kTotal * ctx.Options.LastFraction));

		// Mandatory: error-containing strings.
		var errorIndices = new HashSet<int>();

		for (var idx = 0; idx < n; idx++)
		{
			if (ErrorKeywordDetector.ContainsErrorKeyword(strings[idx]))
			{
				errorIndices.Add(idx);
			}
		}

		// Mandatory: anomalously-long strings (> varianceThreshold σ from mean length).
		var anomalyIndices = new HashSet<int>();
		var lengths = strings.Select(s => (double)s.Length).ToArray();

		if (lengths.Length > 1)
		{
			var meanLen = lengths.Average();
			var variance = lengths.Sum(l => (l - meanLen) * (l - meanLen)) / (lengths.Length - 1);
			var stdLen = Math.Sqrt(variance);

			if (stdLen > 0)
			{
				for (var idx = 0; idx < n; idx++)
				{
					if (Math.Abs(lengths[idx] - meanLen) > ctx.Options.VarianceThreshold * stdLen)
					{
						anomalyIndices.Add(idx);
					}
				}
			}
		}

		// Boundary: first-K and last-K.
		var keepIndices = new HashSet<int>(errorIndices);
		keepIndices.UnionWith(anomalyIndices);

		for (var idx = 0; idx < Math.Min(kFirst, n); idx++)
		{
			keepIndices.Add(idx);
		}

		for (var idx = Math.Max(0, n - kLast); idx < n; idx++)
		{
			keepIndices.Add(idx);
		}

		// Track seen strings for deduplication.
		var seenStrings = new HashSet<string>(StringComparer.Ordinal);

		foreach (var idx in keepIndices.OrderBy(i => i))
		{
			seenStrings.Add(strings[idx]);
		}

		// Fill remaining budget with stride-based, deduplicated samples.
		var remainingBudget = Math.Max(0, kTotal - keepIndices.Count);

		if (remainingBudget > 0)
		{
			var stride = Math.Max(1, (n - 1) / (remainingBudget + 1));

			for (var idx = 0; idx < n; idx += stride)
			{
				if (keepIndices.Count >= kTotal + errorIndices.Count + anomalyIndices.Count)
				{
					break;
				}

				if (!keepIndices.Contains(idx) && seenStrings.Add(strings[idx]))
				{
					keepIndices.Add(idx);
				}
			}
		}

		var result = keepIndices.OrderBy(i => i).Select(i => items[i]).ToArray();

		return (result, result.Length < n);
	}
}
