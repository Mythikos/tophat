using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace TopHat.Compression.CCR;

/// <summary>
/// In-memory <see cref="ICompressionContextStore"/> keyed by a retrieval GUID. Entries expire
/// based on <see cref="CCROptions.RetentionDuration"/>; eviction happens lazily on the next read
/// rather than on a timer, to keep the store self-contained and free of background threads.
/// </summary>
/// <remarks>
/// Singleton-scoped by design — one store spans the entire process so multiple concurrent
/// conversations can share the same instance safely. A distributed backend would implement
/// <see cref="ICompressionContextStore"/> directly without inheriting from this type.
/// </remarks>
public sealed class InMemoryCompressionContextStore : ICompressionContextStore
{
	private readonly IOptions<CCROptions> _options;
	private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
	private readonly TimeProvider _timeProvider;

	public InMemoryCompressionContextStore(IOptions<CCROptions> options, TimeProvider? timeProvider = null)
	{
		ArgumentNullException.ThrowIfNull(options);
		_options = options;
		_timeProvider = timeProvider ?? TimeProvider.System;
	}

	/// <inheritdoc/>
	public void Store(string retrievalKey, IReadOnlyList<JsonNode> droppedItems)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(retrievalKey);
		ArgumentNullException.ThrowIfNull(droppedItems);

		var retention = _options.Value.RetentionDuration;
		var expiresAt = _timeProvider.GetUtcNow() + retention;
		// Deep-clone each node — JsonNode instances have a single parent, so holding a reference
		// to a node that's about to be reserialized as part of a different document would throw.
		var cloned = new List<JsonNode>(droppedItems.Count);

		foreach (var item in droppedItems)
		{
			if (item is not null)
			{
				cloned.Add(item.DeepClone());
			}
		}

		_entries[retrievalKey] = new Entry(cloned, expiresAt);
	}

	/// <inheritdoc/>
	public IReadOnlyList<JsonNode> Retrieve(string retrievalKey, IReadOnlySet<int>? ids, int limit)
	{
		if (string.IsNullOrWhiteSpace(retrievalKey) || limit <= 0)
		{
			return Array.Empty<JsonNode>();
		}

		if (!_entries.TryGetValue(retrievalKey, out var entry))
		{
			return Array.Empty<JsonNode>();
		}

		if (entry.ExpiresAt <= _timeProvider.GetUtcNow())
		{
			// Lazy eviction: drop the expired entry as we discover it.
			_entries.TryRemove(retrievalKey, out _);
			return Array.Empty<JsonNode>();
		}

		var result = new List<JsonNode>(Math.Min(limit, entry.Items.Count));

		foreach (var item in entry.Items)
		{
			if (result.Count >= limit)
			{
				break;
			}

			if (ids is not null)
			{
				if (!TryReadIntId(item, out var itemId) || !ids.Contains(itemId))
				{
					continue;
				}
			}

			// Clone on read so the consumer can splice the returned nodes into its own document
			// without invalidating the stored copy.
			result.Add(item.DeepClone());
		}

		return result;
	}

	private static bool TryReadIntId(JsonNode item, out int id)
	{
		id = 0;

		if (item is not JsonObject obj)
		{
			return false;
		}

		if (!obj.TryGetPropertyValue("id", out var idNode) || idNode is null)
		{
			return false;
		}

		try
		{
			id = idNode.GetValue<int>();
			return true;
		}
		catch (FormatException)
		{
			return false;
		}
		catch (InvalidOperationException)
		{
			return false;
		}
	}

	private readonly record struct Entry(IReadOnlyList<JsonNode> Items, DateTimeOffset ExpiresAt);
}
