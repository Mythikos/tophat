using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TopHat.Compression.CCR;

/// <summary>
/// Cross-orchestrator helper for accumulating provider usage objects across CCR hops and
/// stamping the cumulative total back onto the final response. The user makes one logical
/// SendAsync call but their account is billed for every upstream hop CCR triggers — so the
/// <c>usage</c> field on the response they observe must reflect the sum, not just the last hop.
/// </summary>
/// <remarks>
/// All three providers (Anthropic Messages, OpenAI Chat Completions, OpenAI Responses) expose a
/// top-level <c>usage</c> JSON object whose numeric properties are token counts. The shapes
/// differ in field names but every meaningful value is an additive integer, sometimes nested
/// inside a "details" sub-object. A recursive numeric-sum walk handles all three uniformly.
/// </remarks>
internal static class CCRUsageMerger
{
	/// <summary>
	/// Header stamped on the final response when CCR fired more than once (the initial upstream
	/// call counts as hop 1; only multi-hop responses carry the header). Lets users observe how
	/// many round-trips CCR drove without parsing the body.
	/// </summary>
	public const string HopCountHeader = "X-TopHat-CCR-Hops";

	/// <summary>
	/// Recursively sums numeric properties from <paramref name="incoming"/> into
	/// <paramref name="accumulator"/>. Nested objects (e.g. <c>prompt_tokens_details</c>) are
	/// summed field-by-field. Non-numeric, non-object fields are ignored — provider responses
	/// occasionally include strings (e.g. <c>service_tier</c>) that don't make sense to sum.
	/// </summary>
	public static void Accumulate(JsonObject accumulator, JsonObject? incoming)
	{
		ArgumentNullException.ThrowIfNull(accumulator);

		if (incoming is null)
		{
			return;
		}

		foreach (var kvp in incoming)
		{
			switch (kvp.Value)
			{
				case JsonObject nestedIncoming:
					if (accumulator[kvp.Key] is JsonObject nestedAccum)
					{
						Accumulate(nestedAccum, nestedIncoming);
					}
					else
					{
						var fresh = new JsonObject();
						accumulator[kvp.Key] = fresh;
						Accumulate(fresh, nestedIncoming);
					}
					break;

				case JsonValue val when TryReadLong(val, out var num):
					var existing = 0L;
					if (accumulator.TryGetPropertyValue(kvp.Key, out var existingNode)
						&& existingNode is JsonValue existingVal
						&& TryReadLong(existingVal, out var parsed))
					{
						existing = parsed;
					}
					accumulator[kvp.Key] = existing + num;
					break;
			}
		}
	}

	/// <summary>
	/// Reads a numeric <see cref="JsonValue"/> as <see cref="long"/>, tolerating the backing type
	/// difference between JSON-parsed numbers (typically stored as <c>long</c>) and numbers
	/// constructed programmatically from C# <c>int</c> literals. Returns <c>false</c> for strings,
	/// bools, or fractional numbers that can't round-trip as <c>long</c>.
	/// </summary>
	private static bool TryReadLong(JsonValue val, out long result)
	{
		if (val.TryGetValue<long>(out result))
		{
			return true;
		}

		if (val.TryGetValue<int>(out var asInt))
		{
			result = asInt;
			return true;
		}

		result = 0;
		return false;
	}

	/// <summary>
	/// Replaces the response body's <c>usage</c> object with <paramref name="accumulator"/> and
	/// stamps the <see cref="HopCountHeader"/> response header. Best-effort: if the response body
	/// is not parseable JSON, only the header is added. Caller is responsible for ensuring the
	/// response content has been buffered (so the rewrite doesn't drain a forward-only stream).
	/// </summary>
	public static async ValueTask ApplyAsync(HttpResponseMessage response, JsonObject accumulator, int hopCount, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(response);
		ArgumentNullException.ThrowIfNull(accumulator);

		response.Headers.TryAddWithoutValidation(HopCountHeader, hopCount.ToString(CultureInfo.InvariantCulture));

		if (response.Content is null)
		{
			return;
		}

		byte[] bytes;
		try
		{
			bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (HttpRequestException)
		{
			return;
		}

		JsonObject? body;
		try
		{
			body = JsonNode.Parse(bytes) as JsonObject;
		}
		catch (JsonException)
		{
			return;
		}

		if (body is null)
		{
			return;
		}

		body["usage"] = accumulator.DeepClone();

		var newBytes = Encoding.UTF8.GetBytes(body.ToJsonString());
		var originalContentHeaders = response.Content.Headers;
		var newContent = new ByteArrayContent(newBytes);

		foreach (var header in originalContentHeaders)
		{
			// Skip Content-Length — ByteArrayContent computes it correctly from the new bytes,
			// and stamping a stale value triggers an HttpClient mismatch error on read.
			if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			newContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
		}

		response.Content.Dispose();
		response.Content = newContent;
	}
}
