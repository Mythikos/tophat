using System.Text.Json.Nodes;
using TopHat.Transforms.JsonContext;

namespace TopHat.Transforms.JsonContext.Strategies;

/// <summary>
/// Recurses into a JsonObject to find and compress nested arrays and objects.
/// Mirrors the dict-traversal branch in headroom's _process_value.
/// </summary>
internal static class NestedRecursionStrategy
{
	private const int MaxDepth = 50;

	/// <summary>
	/// Recursively processes <paramref name="node"/>, compressing arrays where appropriate.
	/// Returns the (possibly mutated) node and whether any compression occurred.
	/// </summary>
	public static (JsonNode? Result, bool WasModified) Process(
		JsonNode? node,
		JsonCompressionContext ctx,
		int depth = 0)
	{
		if (depth >= MaxDepth || node is null)
		{
			return (node, false);
		}

		switch (node)
		{
			case JsonArray arr:
				return ProcessArray(arr, ctx, depth);

			case JsonObject obj:
				return ProcessObject(obj, ctx, depth);

			default:
				return (node, false);
		}
	}

	private static (JsonNode? Result, bool WasModified) ProcessArray(
		JsonArray arr,
		JsonCompressionContext ctx,
		int depth)
	{
		var dispatcher = new JsonTypeDispatcher(ctx);
		var (kept, wasModified) = dispatcher.Compress(arr, depth);

		if (wasModified)
		{
			var newArr = new JsonArray();

			foreach (var item in kept)
			{
				newArr.Add(item?.DeepClone());
			}

			return (newArr, true);
		}

		// Even if this array wasn't compressed, recurse into its object items.
		var anyChildModified = false;

		for (var idx = 0; idx < arr.Count; idx++)
		{
			if (arr[idx] is JsonObject || arr[idx] is JsonArray)
			{
				var (childResult, childModified) = Process(arr[idx], ctx, depth + 1);

				if (childModified)
				{
					arr[idx] = childResult?.DeepClone();
					anyChildModified = true;
				}
			}
		}

		return (arr, anyChildModified);
	}

	private static (JsonNode? Result, bool WasModified) ProcessObject(
		JsonObject obj,
		JsonCompressionContext ctx,
		int depth)
	{
		var anyModified = false;

		foreach (var key in obj.Select(kvp => kvp.Key).ToArray())
		{
			var val = obj[key];

			if (val is JsonArray || val is JsonObject)
			{
				var (processed, modified) = Process(val, ctx, depth + 1);

				if (modified)
				{
					obj[key] = processed?.DeepClone();
					anyModified = true;
				}
			}
		}

		return (obj, anyModified);
	}
}
