using System.Text.Json.Nodes;
using TopHat.Transforms.JsonContext.Common;

namespace TopHat.Transforms.JsonContext.Strategies;

/// <summary>
/// Compresses a JSON array of numeric values using statistical outlier and change-point preservation.
/// Port of headroom's SmartCrusher._crush_number_array.
/// </summary>
internal static class NumberArrayCompressionStrategy
{
	/// <summary>
	/// Compresses <paramref name="items"/> and returns a subset preserving
	/// outliers, change points, and boundary items.
	/// Returns the original array unchanged if insufficient compression gain would be achieved.
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

		var itemStrings = items.Select(SerializeItem).ToArray();
		var kTotal = AdaptiveKCalculator.Compute(itemStrings, minK: 3, maxK: ctx.Options.MaxItemsAfterCrush);

		if (kTotal >= n)
		{
			return (items, false);
		}

		var kFirst = Math.Max(1, (int)Math.Round(kTotal * ctx.Options.FirstFraction));
		var kLast = Math.Max(1, (int)Math.Round(kTotal * ctx.Options.LastFraction));

		// Extract finite numeric values (with their original indices) for statistics.
		var finiteValues = new List<(int Index, double Value)>(n);

		for (var idx = 0; idx < n; idx++)
		{
			if (TryGetDouble(items[idx], out var val) && double.IsFinite(val))
			{
				finiteValues.Add((idx, val));
			}
		}

		var keepIndices = new HashSet<int>();

		// Boundary: first-K and last-K.
		for (var idx = 0; idx < Math.Min(kFirst, n); idx++)
		{
			keepIndices.Add(idx);
		}

		for (var idx = Math.Max(0, n - kLast); idx < n; idx++)
		{
			keepIndices.Add(idx);
		}

		if (finiteValues.Count > 1)
		{
			var mean = finiteValues.Average(t => t.Value);
			var variance = finiteValues.Sum(t => (t.Value - mean) * (t.Value - mean)) / (finiteValues.Count - 1);
			var stdDev = Math.Sqrt(variance);
			var threshold = ctx.Options.VarianceThreshold * stdDev;

			// Outliers: values more than varianceThreshold σ from mean.
			if (stdDev > 0)
			{
				foreach (var (idx, val) in finiteValues)
				{
					if (Math.Abs(val - mean) > threshold)
					{
						keepIndices.Add(idx);
					}
				}
			}

			// Change points.
			if (ctx.Options.PreserveChangePoints && finiteValues.Count >= 10)
			{
				var onlyValues = finiteValues.Select(t => t.Value).ToList();
				var changePoints = ChangePointDetector.Detect(onlyValues, ctx.Options.VarianceThreshold);

				foreach (var cp in changePoints)
				{
					keepIndices.Add(finiteValues[cp].Index);
				}
			}
		}

		// Fill remaining budget with stride-based samples.
		var remainingBudget = Math.Max(0, kTotal - keepIndices.Count);

		if (remainingBudget > 0)
		{
			var stride = Math.Max(1, (n - 1) / (remainingBudget + 1));

			for (var idx = 0; idx < n; idx += stride)
			{
				if (keepIndices.Count >= kTotal)
				{
					break;
				}

				keepIndices.Add(idx);
			}
		}

		var result = keepIndices.OrderBy(i => i).Select(i => items[i]).ToArray();

		return (result, result.Length < n);
	}

	private static string SerializeItem(JsonNode? node)
	{
		return node?.ToJsonString() ?? "null";
	}

	private static bool TryGetDouble(JsonNode? node, out double value)
	{
		if (node is JsonValue jv && jv.TryGetValue<double>(out value))
		{
			return true;
		}

		value = 0;
		return false;
	}
}
