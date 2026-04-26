using System.Text.Json.Nodes;
using TopHat.Transforms.JsonContext.Strategies;

namespace TopHat.Transforms.JsonContext;

/// <summary>
/// Classifies a JsonArray by its element types and dispatches to the appropriate compression strategy.
/// Mirrors headroom's _classify_array + _process_value dispatch logic.
/// </summary>
internal sealed class JsonTypeDispatcher
{
	private readonly JsonCompressionContext _ctx;

	public JsonTypeDispatcher(JsonCompressionContext ctx)
	{
		_ctx = ctx;
	}

	/// <summary>
	/// Classifies and compresses <paramref name="array"/>.
	/// Returns the kept items and whether any compression occurred.
	/// </summary>
	public (IReadOnlyList<JsonNode?> Kept, bool WasModified) Compress(JsonArray array, int depth = 0)
	{
		var items = array.ToArray();

		if (items.Length < _ctx.Options.MinItemsToAnalyze)
		{
			return (items, false);
		}

		var kind = Classify(array);  // Materialize to IReadOnlyList for strategies.

		return kind switch
		{
			ArrayKind.DictArray => DictArrayCompressionStrategy.Compress(items, _ctx),
			ArrayKind.StringArray => StringArrayCompressionStrategy.Compress(items, _ctx),
			ArrayKind.NumberArray => NumberArrayCompressionStrategy.Compress(items, _ctx),
			ArrayKind.MixedArray => MixedArrayCompressionStrategy.Compress(items, _ctx),
			_ => (items, false), // BOOL_ARRAY, NESTED_ARRAY, EMPTY — no compression
		};
	}

	private static ArrayKind Classify(JsonArray array)
	{
		if (array.Count == 0)
		{
			return ArrayKind.Empty;
		}

		var dictCount = 0;
		var stringCount = 0;
		var numberCount = 0;
		var boolCount = 0;
		var nullCount = 0;
		var nestedCount = 0;

		foreach (var node in array)
		{
			switch (node)
			{
				case null:
					nullCount++;
					break;

				case JsonObject:
					dictCount++;
					break;

				case JsonArray:
					nestedCount++;
					break;

				case JsonValue jv when jv.TryGetValue<bool>(out _):
					boolCount++;
					break;

				case JsonValue jv when jv.TryGetValue<double>(out _) || jv.TryGetValue<long>(out _):
					numberCount++;
					break;

				case JsonValue jv when jv.TryGetValue<string>(out _):
					stringCount++;
					break;
			}
		}

		var n = array.Count;
		var nonNull = n - nullCount;

		if (nonNull == 0)
		{
			return ArrayKind.Empty;
		}

		// Dominant type: 80%+ of non-null elements must be the same type.
		const double dominanceThreshold = 0.8;

		if (dictCount >= nonNull * dominanceThreshold)
		{
			return ArrayKind.DictArray;
		}

		if (stringCount >= nonNull * dominanceThreshold)
		{
			return ArrayKind.StringArray;
		}

		if (numberCount >= nonNull * dominanceThreshold)
		{
			return ArrayKind.NumberArray;
		}

		if (boolCount >= nonNull * dominanceThreshold)
		{
			return ArrayKind.BoolArray;
		}

		if (nestedCount >= nonNull * dominanceThreshold)
		{
			return ArrayKind.NestedArray;
		}

		return ArrayKind.MixedArray;
	}

	private enum ArrayKind
	{
		Empty,
		DictArray,
		StringArray,
		NumberArray,
		BoolArray,
		NestedArray,
		MixedArray,
	}
}
