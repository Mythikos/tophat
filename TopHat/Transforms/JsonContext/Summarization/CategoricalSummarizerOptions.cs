namespace TopHat.Transforms.JsonContext.Summarization;

/// <summary>
/// Options controlling <see cref="CategoricalSummarizer"/> behavior.
/// Defaults port directly from headroom's compression_summary.py (_CATEGORY_FIELDS, _NOTABLE_PATTERNS).
/// </summary>
public sealed class CategoricalSummarizerOptions
{
	/// <summary>
	/// Field names inspected (in order) to categorize dropped items. The first matching field with a
	/// short string value is used for that item.
	/// </summary>
	public string[] CategoryFields { get; set; } = new[]
	{
		"type", "status", "kind", "category", "level", "severity",
		"state", "phase", "action", "event_type", "log_level",
		"result", "outcome",
	};

	/// <summary>Maximum number of distinct categories to include in the summary. Default: 5.</summary>
	public int MaxCategories { get; set; } = 5;

	/// <summary>Maximum number of notable (error/failure/warning) items to call out by id/name. Default: 3.</summary>
	public int MaxNotable { get; set; } = 3;

	/// <summary>
	/// Regex matched (case-insensitive) against each dropped item's JSON text to flag it as notable.
	/// </summary>
	public string NotablePattern { get; set; } =
		@"error|fail|critical|warning|exception|crash|timeout|denied|rejected|invalid";

	/// <summary>Maximum character length of a category value before it's rejected as too verbose. Default: 50.</summary>
	public int MaxCategoryValueLength { get; set; } = 50;

	/// <summary>
	/// Field names preferred (in order) for labeling notable items. First non-empty value wins.
	/// </summary>
	public string[] NotableLabelFields { get; set; } = new[] { "name", "id", "path" };
}
