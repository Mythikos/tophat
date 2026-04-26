using System.Text.Json.Nodes;

namespace TopHat.Transforms.JsonContext.Summarization;

/// <summary>
/// Produces a short, human-readable summary string describing items that were dropped during compression.
/// The summary is embedded in the compressed tool_result so the LLM can reason about what's missing
/// without needing to fetch the originals.
/// </summary>
/// <remarks>
/// Implementations should return <c>null</c> when they have nothing useful to contribute for a given
/// set of dropped items (e.g., a numeric summarizer seeing a list of pure strings). Non-null fragments
/// from multiple summarizers are joined with <c>"; "</c>.
/// </remarks>
public interface IDroppedItemsSummarizer
{
	/// <summary>
	/// Generates a summary fragment for <paramref name="dropped"/>.
	/// </summary>
	/// <param name="dropped">The JSON objects that were removed by compression.</param>
	/// <param name="context">Summarization context including kept-count and total-count.</param>
	/// <returns>Summary fragment, or <c>null</c> if this summarizer has nothing to say.</returns>
	string? Summarize(IReadOnlyList<JsonObject> dropped, SummarizationContext context);
}
