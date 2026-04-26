using System.Text.Json.Nodes;
using TopHat.Relevance;
using TopHat.Transforms.JsonContext.Summarization;

namespace TopHat.Transforms.JsonContext;

/// <summary>
/// Shared state passed to all compression strategies for a single request.
/// </summary>
internal sealed class JsonCompressionContext
{
	/// <summary>
	/// Relevance query context derived from recent conversation messages.
	/// Empty string if no context was extractable.
	/// </summary>
	public string QueryContext { get; init; } = string.Empty;

	/// <summary>Scorer used to rank items by relevance to <see cref="QueryContext"/>.</summary>
	public required IRelevanceScorer Scorer { get; init; }

	/// <summary>Options controlling thresholds and preservation fractions.</summary>
	public required JsonContextCompressorOptions Options { get; init; }

	/// <summary>
	/// Summarizers run against dropped items to produce a metadata fragment embedded alongside the
	/// kept items. Empty when no summarizers are registered.
	/// </summary>
	public IReadOnlyList<IDroppedItemsSummarizer> Summarizers { get; init; } = Array.Empty<IDroppedItemsSummarizer>();

	/// <summary>
	/// Optional callback invoked when a strategy drops items from a tool_result array. Receives
	/// the dropped items in their original order and returns a retrieval key (GUID string) that
	/// the strategy embeds in the compression metadata so the model can request the items back
	/// via CCR (<c>tophat_retrieve</c>). When null — the default — CCR is inactive and strategies
	/// write no retrieval key.
	/// </summary>
	/// <remarks>
	/// Scoped per tool_result: the compressor transform invokes this once per compressed array,
	/// so different tool_results in the same request receive distinct retrieval keys.
	/// </remarks>
	public Func<IReadOnlyList<JsonNode>, string?>? RegisterDroppedItemsForRetrieval { get; init; }
}
