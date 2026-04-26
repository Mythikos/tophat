using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TopHat.Providers;

namespace TopHat.Transforms;

/// <summary>
/// Detects cache-prefix mutations across the three target wire formats. Returns a SHA-256
/// hex digest of the cache-relevant prefix; a different digest before vs after a transform
/// means the transform busted the cache lineage.
/// </summary>
/// <remarks>
/// <para>
/// <b>Anthropic</b> uses explicit <c>cache_control</c> markers in the request body. The
/// "prefix" is everything before the first such marker; any change to that region shifts the
/// model's cache key. Detection is a string-level <c>IndexOf</c> + hash.
/// </para>
/// <para>
/// <b>OpenAI</b> (both Chat Completions and Responses) uses automatic prefix caching with no
/// request-side markers. The cache-relevant prefix is the conversation history MINUS the
/// last entry — the last <c>messages[N]</c> / <c>input[N]</c> is typically the new turn the
/// user is submitting and is expected to differ across requests, while everything before it
/// is the prefix that was cached on prior turns. A transform that mutates anything in the
/// non-last region risks busting cache; mutations confined to the last entry are benign.
/// </para>
/// </remarks>
internal static class CachePrefixHasher
{
	private const string AnthropicMarkerToken = "\"cache_control\"";

	/// <summary>
	/// Returns a SHA-256 hex hash of the cache-relevant prefix for the given body and target,
	/// or <c>null</c> when the target has no detectable cache region (no markers / single-item
	/// conversation / unsupported target).
	/// </summary>
	public static string? Hash(JsonObject body, TopHatTarget target)
	{
		ArgumentNullException.ThrowIfNull(body);

		return target switch
		{
			TopHatTarget.AnthropicMessages => HashAnthropic(body),
			TopHatTarget.OpenAIChatCompletions => HashOpenAIPrefix(body, "messages"),
			TopHatTarget.OpenAIResponses => HashOpenAIPrefix(body, "input"),
			_ => null,
		};
	}

	/// <summary>
	/// Anthropic: hash the canonical-serialized body up to the first <c>cache_control</c>
	/// marker. Any mutation to that region shifts the model's cache key for the marker.
	/// </summary>
	private static string? HashAnthropic(JsonObject body)
	{
		var bodyText = body.ToJsonString();
		if (string.IsNullOrEmpty(bodyText))
		{
			return null;
		}

		var idx = bodyText.IndexOf(AnthropicMarkerToken, StringComparison.Ordinal);
		if (idx < 0)
		{
			return null;
		}

		var prefixBytes = Encoding.UTF8.GetBytes(bodyText, 0, idx);
		return Convert.ToHexString(SHA256.HashData(prefixBytes));
	}

	/// <summary>
	/// OpenAI (Chat Completions / Responses): hash all entries in <paramref name="arrayKey"/>
	/// except the last. The trailing entry is the new turn; everything before it is the
	/// cache-relevant prefix that should not be mutated by transforms.
	/// </summary>
	private static string? HashOpenAIPrefix(JsonObject body, string arrayKey)
	{
		if (body[arrayKey] is not JsonArray arr || arr.Count <= 1)
		{
			// No detectable prefix: empty conversation or single-message request.
			return null;
		}

		// DeepClone the prefix entries into a new array so we serialize ONLY the prefix —
		// the last entry's content can't influence the hash even via shared references.
		var prefix = new JsonArray();
		for (var i = 0; i < arr.Count - 1; i++)
		{
			prefix.Add(arr[i]?.DeepClone());
		}

		var bytes = Encoding.UTF8.GetBytes(prefix.ToJsonString());
		return Convert.ToHexString(SHA256.HashData(bytes));
	}
}
