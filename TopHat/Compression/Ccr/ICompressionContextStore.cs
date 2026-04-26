using System.Text.Json.Nodes;

namespace TopHat.Compression.CCR;

/// <summary>
/// Stores items that were dropped by the JSON context compressor so the model can retrieve them
/// mid-turn via the <c>tophat_retrieve</c> tool. Populated by
/// <c>JsonContextCompressorTransform</c>; queried by the CCR orchestrator when the
/// model calls the retrieval tool.
/// </summary>
/// <remarks>
/// <para>
/// Each stored entry is keyed by a GUID-backed retrieval key generated at compression time and
/// surfaced to the model via the <c>_tophat_compression.retrieval_key</c> metadata field. The
/// store is deliberately in-memory by default — single-process deployments don't need more, and
/// a distributed backend (Redis, SQL) is a future concern that can implement this same interface.
/// </para>
/// <para>
/// Entries are subject to TTL eviction. A tool_result compressed early in a long conversation
/// may be un-retrievable by the time the model decides it needs the elided items. This is
/// intentional — CCR is a rescue valve for edge cases, not a guaranteed-durable state channel.
/// </para>
/// </remarks>
public interface ICompressionContextStore
{
	/// <summary>
	/// Stores the dropped items for a given compression invocation. The caller is expected to
	/// generate <paramref name="retrievalKey"/> at compression time and surface it to the model
	/// via the compression metadata marker.
	/// </summary>
	/// <param name="retrievalKey">A unique identifier (typically a GUID) for this tool_result's compression.</param>
	/// <param name="droppedItems">The items that were elided from the compressed payload, in their original order.</param>
	void Store(string retrievalKey, IReadOnlyList<JsonNode> droppedItems);

	/// <summary>
	/// Retrieves dropped items by <paramref name="retrievalKey"/>, optionally filtered to a
	/// specific subset of integer IDs. Returns an empty list if the key is unknown, expired, or
	/// no items match the filter.
	/// </summary>
	/// <param name="retrievalKey">The key originally assigned by <see cref="Store"/>.</param>
	/// <param name="ids">If non-null, only items whose top-level <c>id</c> field (int) is in this set are returned. If null, all stored items are returned up to <paramref name="limit"/>.</param>
	/// <param name="limit">Maximum number of items to return. Callers should bound this to keep the follow-up request payload sane.</param>
	IReadOnlyList<JsonNode> Retrieve(string retrievalKey, IReadOnlySet<int>? ids, int limit);
}
