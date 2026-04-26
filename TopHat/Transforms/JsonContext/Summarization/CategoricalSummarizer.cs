using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TopHat.Transforms.JsonContext.Summarization;

/// <summary>
/// Emits a categorical summary of dropped items by counting values of common category fields
/// (type/status/level/severity/etc.) and calling out items whose JSON text matches a notable-keyword
/// pattern (error/fail/warning/etc.).
/// </summary>
/// <remarks>
/// Port of headroom's compression_summary.summarize_dropped_items (Strategy 1 + Strategy 2).
/// Returns <c>null</c> only when no categories and no notable items are found AND no fallback fields
/// can be inferred — otherwise produces a non-null fragment.
/// </remarks>
public sealed class CategoricalSummarizer : IDroppedItemsSummarizer
{
	private static readonly HashSet<string> s_fallbackFieldExcludes = new (StringComparer.OrdinalIgnoreCase)
	{
		"id", "name", "path", "url", "href", "email",
	};
	private static readonly Regex s_urlLikePattern = new (@"^https?://|^/[a-z]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

	private readonly CategoricalSummarizerOptions _options;
	private readonly Regex _notablePattern;

	public CategoricalSummarizer(IOptions<CategoricalSummarizerOptions> options)
	{
		this._options = options?.Value ?? throw new ArgumentNullException(nameof(options));
		this._notablePattern = new Regex(this._options.NotablePattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
	}

	/// <inheritdoc/>
	public string? Summarize(IReadOnlyList<JsonObject> dropped, SummarizationContext context)
	{
		ArgumentNullException.ThrowIfNull(dropped);
		ArgumentNullException.ThrowIfNull(context);

		if (dropped.Count == 0)
		{
			return null;
		}

		var categories = this.CategorizeByFields(dropped);
		var notable = this.FindNotableItems(dropped);

		var parts = new List<string>(3);

		// Suppress the categorical fragment when every observed category has count 1 — this
		// happens when the "category" is effectively a unique id (e.g., username=user_16), which
		// produces high-noise, low-signal output. The notable + fallback paths still run.
		var hasSignal = categories.Values.Any(count => count >= 2);

		if (categories.Count > 0 && hasSignal)
		{
			var catStrings = categories
				.OrderByDescending(kvp => kvp.Value)
				.Take(this._options.MaxCategories)
				.Select(kvp => $"{kvp.Value} {kvp.Key}");
			parts.Add(string.Join(", ", catStrings));
		}

		if (notable.Count > 0)
		{
			parts.Add("notable: " + string.Join("; ", notable));
		}

		if (parts.Count == 0)
		{
			// Fallback: list a few common field names so the LLM at least knows what was there.
			var keys = CommonKeys(dropped, maxKeys: 5);

			if (keys.Count > 0)
			{
				parts.Add("fields: " + string.Join(", ", keys));
			}
		}

		return parts.Count == 0 ? null : string.Join("; ", parts);
	}

	private Dictionary<string, int> CategorizeByFields(IReadOnlyList<JsonObject> dropped)
	{
		var counts = new Dictionary<string, int>(StringComparer.Ordinal);

		foreach (var item in dropped)
		{
			var categorized = TryCategorizeFromCategoryFields(item, counts);

			if (!categorized)
			{
				TryCategorizeFromFallback(item, counts);
			}
		}

		return counts;
	}

	private bool TryCategorizeFromCategoryFields(JsonObject item, Dictionary<string, int> counts)
	{
		foreach (var field in this._options.CategoryFields)
		{
			if (!item.TryGetPropertyValue(field, out var node) || node is null)
			{
				continue;
			}

			var val = ExtractShortString(node, this._options.MaxCategoryValueLength);

			if (val is null)
			{
				continue;
			}

			IncrementCount(counts, val);
			return true;
		}

		return false;
	}

	private static void TryCategorizeFromFallback(JsonObject item, Dictionary<string, int> counts)
	{
		foreach (var kvp in item)
		{
			if (s_fallbackFieldExcludes.Contains(kvp.Key))
			{
				continue;
			}

			if (kvp.Value is not JsonValue jv || !jv.TryGetValue<string>(out var s))
			{
				continue;
			}

			var clean = CleanString(s);

			if (clean.Length <= 2 || clean.Length >= 30)
			{
				continue;
			}

			if (s_urlLikePattern.IsMatch(clean))
			{
				continue;
			}

			IncrementCount(counts, $"{kvp.Key}={clean}");
			return;
		}
	}

	private List<string> FindNotableItems(IReadOnlyList<JsonObject> dropped)
	{
		var notable = new List<string>(this._options.MaxNotable);

		foreach (var item in dropped)
		{
			if (notable.Count >= this._options.MaxNotable)
			{
				break;
			}

			var itemText = item.ToJsonString();

			if (itemText.Length > 500)
			{
				itemText = itemText[..500];
			}

			var match = this._notablePattern.Match(itemText);

			if (!match.Success)
			{
				continue;
			}

			var label = GetNotableLabel(item, this._options.NotableLabelFields);
			notable.Add(label is null ? match.Value.ToLowerInvariant() : $"{label} ({match.Value.ToLowerInvariant()})");
		}

		return notable;
	}

	private static string? GetNotableLabel(JsonObject item, string[] labelFields)
	{
		foreach (var field in labelFields)
		{
			if (!item.TryGetPropertyValue(field, out var node) || node is not JsonValue jv)
			{
				continue;
			}

			// JsonValue.TryGetValue<T> can be picky about numeric backing types; ToJsonString()
			// reliably returns the JSON literal ("2" for int, "\"alice\"" for string). Strip
			// surrounding quotes for strings so the label reads naturally.
			var raw = jv.ToJsonString();

			if (string.IsNullOrEmpty(raw))
			{
				continue;
			}

			if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
			{
				raw = raw[1..^1];
			}

			if (string.IsNullOrWhiteSpace(raw))
			{
				continue;
			}

			var clean = CleanString(raw);

			return clean.Length > 50 ? clean[..50] : clean;
		}

		return null;
	}

	private static List<string> CommonKeys(IReadOnlyList<JsonObject> items, int maxKeys)
	{
		var keyCounts = new Dictionary<string, int>(StringComparer.Ordinal);

		foreach (var item in items)
		{
			foreach (var kvp in item)
			{
				IncrementCount(keyCounts, kvp.Key);
			}
		}

		return keyCounts
			.OrderByDescending(kvp => kvp.Value)
			.Take(maxKeys)
			.Select(kvp => kvp.Key)
			.ToList();
	}

	private static string? ExtractShortString(JsonNode node, int maxLength)
	{
		if (node is not JsonValue jv || !jv.TryGetValue<string>(out var s))
		{
			return null;
		}

		var clean = CleanString(s);

		if (clean.Length == 0 || clean.Length >= maxLength)
		{
			return null;
		}

		return clean;
	}

	private static string CleanString(string value)
	{
		if (value.IndexOf('\n') < 0 && value.IndexOf('\r') < 0)
		{
			return value.Trim();
		}

		var sb = new StringBuilder(value.Length);

		foreach (var c in value)
		{
			if (c == '\n')
			{
				sb.Append(' ');
			}
			else if (c == '\r')
			{
				// strip
			}
			else
			{
				sb.Append(c);
			}
		}

		return sb.ToString().Trim();
	}

	private static void IncrementCount(Dictionary<string, int> counts, string key)
	{
		counts[key] = counts.TryGetValue(key, out var existing) ? existing + 1 : 1;
	}
}
