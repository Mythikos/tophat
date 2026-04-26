using System.Text.Json.Nodes;
using TopHat.Transforms.JsonContext;

namespace TopHat.Transforms.JsonContext.Strategies;

/// <summary>
/// Compresses a mixed-type JSON array by grouping items by type and applying the appropriate
/// strategy to each group. Port of headroom's SmartCrusher._crush_mixed_array.
/// </summary>
internal static class MixedArrayCompressionStrategy
{
	public static (IReadOnlyList<JsonNode?> Kept, bool WasModified) Compress(
		IReadOnlyList<JsonNode?> items,
		JsonCompressionContext ctx)
	{
		var n = items.Count;

		if (n < ctx.Options.MinItemsToAnalyze)
		{
			return (items, false);
		}

		// Group items by type, preserving original indices.
		var dictGroup = new List<(int Index, JsonNode? Item)>();
		var stringGroup = new List<(int Index, JsonNode? Item)>();
		var numberGroup = new List<(int Index, JsonNode? Item)>();
		var otherGroup = new List<(int Index, JsonNode? Item)>();

		for (var idx = 0; idx < n; idx++)
		{
			var item = items[idx];

			switch (item)
			{
				case JsonObject:
					dictGroup.Add((idx, item));
					break;

				case JsonValue jv when jv.TryGetValue<string>(out _):
					stringGroup.Add((idx, item));
					break;

				case JsonValue jv when jv.TryGetValue<double>(out _) || jv.TryGetValue<long>(out _):
					numberGroup.Add((idx, item));
					break;

				default:
					otherGroup.Add((idx, item));
					break;
			}
		}

		// Compress each sufficiently-large group and collect kept indices.
		var keptIndices = new HashSet<int>();

		CompressGroup(dictGroup, ctx, keptIndices, DictArrayCompressionStrategy.Compress);
		CompressGroup(stringGroup, ctx, keptIndices, StringArrayCompressionStrategy.Compress);
		CompressGroup(numberGroup, ctx, keptIndices, NumberArrayCompressionStrategy.Compress);

		// Keep all items in small groups and the "other" group.
		foreach (var (idx, _) in otherGroup)
		{
			keptIndices.Add(idx);
		}

		if (dictGroup.Count < ctx.Options.MinItemsToAnalyze)
		{
			foreach (var (idx, _) in dictGroup) keptIndices.Add(idx);
		}

		if (stringGroup.Count < ctx.Options.MinItemsToAnalyze)
		{
			foreach (var (idx, _) in stringGroup) keptIndices.Add(idx);
		}

		if (numberGroup.Count < ctx.Options.MinItemsToAnalyze)
		{
			foreach (var (idx, _) in numberGroup) keptIndices.Add(idx);
		}

		var result = keptIndices.OrderBy(i => i).Select(i => items[i]).ToArray();

		return (result, result.Length < n);
	}

	private static void CompressGroup(
		List<(int Index, JsonNode? Item)> group,
		JsonCompressionContext ctx,
		HashSet<int> keptIndices,
		Func<IReadOnlyList<JsonNode?>, JsonCompressionContext, (IReadOnlyList<JsonNode?> Kept, bool WasModified)> compress)
	{
		if (group.Count < ctx.Options.MinItemsToAnalyze)
		{
			return;
		}

		var groupItems = group.Select(t => t.Item).ToArray();
		var (kept, _) = compress(groupItems, ctx);

		// Re-map kept group positions back to original indices.
		var keptSet = new HashSet<JsonNode?>(kept, ReferenceEqualityComparer.Instance);

		foreach (var (idx, item) in group)
		{
			if (keptSet.Contains(item))
			{
				keptIndices.Add(idx);
			}
		}
	}
}
