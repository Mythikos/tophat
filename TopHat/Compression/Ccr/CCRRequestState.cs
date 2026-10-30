using System.Text.Json;
using System.Text.Json.Nodes;
using TopHat.Providers;
using TopHat.Transforms.JsonContext.Messages;

namespace TopHat.Compression.CCR;

/// <summary>Tracks reversible compression changes within a single handler invocation.</summary>
internal sealed class CCRRequestState
{
	private static readonly HttpRequestOptionsKey<RetrySnapshot?> s_retrySnapshotKey = new("TopHat.CCR.RetryProvenance");
	private readonly List<PayloadChange> _payloadChanges = new();
	private readonly HashSet<string> _retrievalKeys = new(StringComparer.Ordinal);
	private readonly HashSet<string> _replayedRetrievalKeys = new(StringComparer.Ordinal);
	private JsonArray? _originalDefinitions;

	/// <summary>Recognizes canonical definitions before transforms decorate them and preserves their prior retrieval contract.</summary>
	public void CaptureInitialDefinition(JsonObject? body, TopHatTarget target)
	{
		if (body is null || CCRRequestPolicy.HasCallerToolCollision(body, target))
		{
			return;
		}
		var definitions = NamedDefinitions(body, target);
		if (definitions.Count == 0)
		{
			return;
		}
		this._originalDefinitions = definitions;
		this._replayedRetrievalKeys.UnionWith(MetadataKeys(body, target));
		this.OwnsToolDefinition = true;
	}

	/// <summary>Identifies requests carrying private retry provenance even after routing or handler configuration changes.</summary>
	public static bool HasRetryProvenance(HttpRequestMessage request) => request.Options.TryGetValue(s_retrySnapshotKey, out var snapshot) && snapshot is not null;

	/// <summary>Restores a proven prior attempt before transforms run again; copied Options alone never establish ownership.</summary>
	public static bool RestoreRetry(HttpRequestMessage request, JsonObject? body, TopHatTarget target)
	{
		if (!request.Options.TryGetValue(s_retrySnapshotKey, out var snapshot) || snapshot is null)
		{
			return false;
		}
		if (body is null)
		{
			throw new HttpRequestException("A retry carrying CCR provenance is no longer inspectable; its private artifacts cannot be forwarded safely.");
		}
		var changed = snapshot.Restore(body, target);
		request.Options.Set(s_retrySnapshotKey, null);
		return changed;
	}

	/// <summary>Publishes a private immutable snapshot for retry handlers that reuse the request or copy its Options.</summary>
	public void PreserveForRetry(HttpRequestMessage request, JsonObject body, TopHatTarget target)
	{
		var definitions = NamedDefinitions(body, target);
		request.Options.Set(s_retrySnapshotKey, this.OwnsToolDefinition && definitions.Count > 0
			? new RetrySnapshot(this, target, definitions)
			: null);
	}

	/// <summary>Whether injection created the tools property rather than extending an existing array.</summary>
	public bool CreatedToolsProperty { get; set; }

	/// <summary>Whether this invocation injected or recognized the canonical internal tool definition.</summary>
	public bool OwnsToolDefinition { get; set; }

	/// <summary>Whether compression has issued at least one reversible payload change.</summary>
	public bool HasCompressedPayloads => this._payloadChanges.Count > 0;

	/// <summary>Records keys whose data must not become inaccessible after a later transform.</summary>
	public void RecordKey(string retrievalKey) => this._retrievalKeys.Add(retrievalKey);

	/// <summary>Records a payload rewrite, retaining the original across repeated compression passes.</summary>
	public void RecordCompression(ToolResultRef result, string compressed)
	{
		var address = Describe(result);
		var change = this._payloadChanges.FirstOrDefault(existing => ReferenceEquals(existing.Owner, result.Owner) && existing.Key == result.Key);
		if (change is null && address.CallId is not null)
		{
			var previous = this._payloadChanges.Where(existing => existing.Address == address && existing.Matches(result.Text)
				&& !ReferenceEquals(Root(existing.Owner), Root(result.Owner))).ToArray();
			if (previous.Length == 1)
			{
				change = previous[0];
			}
		}
		if (change is null)
		{
			change = new PayloadChange(result, address);
			this._payloadChanges.Add(change);
		}
		else if (!change.Matches(result.Text))
		{
			// An intervening transform owns this edit; restoring the earlier source would erase it.
			change.CanRestore = false;
		}
		change.Versions.Add(compressed);
	}

	/// <summary>Restores only TopHat's changes, preserving other transform mutations.</summary>
	public bool RestorePayloads(JsonObject body, TopHatTarget target)
	{
		if (this._payloadChanges.Count == 0)
		{
			return false;
		}

		var slots = ToolResultLocator.Find(body, target).Select(result => new PayloadSlot(result, Describe(result))).ToArray();
		var assigned = new HashSet<PayloadSlot>();
		var restorations = new List<(ToolResultRef Result, string Original)>();
		foreach (var change in this._payloadChanges)
		{
			if (!change.CanRestore)
			{
				throw UnresolvablePayloadException();
			}
			var live = slots.FirstOrDefault(slot => ReferenceEquals(slot.Result.Owner, change.Owner) && slot.Result.Key == change.Key);
			PayloadSlot? selected = null;
			if (live is not null && !assigned.Contains(live) && change.Matches(live.Result.Text))
			{
				selected = live;
			}
			else if (change.Address.CallId is not null)
			{
				var matches = slots.Where(slot => !assigned.Contains(slot) && slot.Address.CallId == change.Address.CallId
					&& slot.Address.IdProperty == change.Address.IdProperty && change.Matches(slot.Result.Text)).ToArray();
				if (matches.Length == 1)
				{
					selected = matches[0];
				}
				else if (matches.Length > 1)
				{
					// Positions alone cannot disambiguate reordered equal outputs with different originals.
					var ambiguous = matches.Any(slot => this._payloadChanges.Any(other => other.Address.CallId == change.Address.CallId
						&& other.Address.IdProperty == change.Address.IdProperty && other.Original != change.Original && other.Matches(slot.Result.Text)));
					if (!ambiguous)
					{
						selected = matches.FirstOrDefault(slot => slot.Address.Path == change.Address.Path) ?? matches[0];
					}
				}
			}
			if (selected is null)
			{
				throw UnresolvablePayloadException();
			}
			assigned.Add(selected);
			if (selected.Result.Text != change.Original)
			{
				restorations.Add((selected.Result, change.Original));
			}
		}

		if (slots.Any(slot => !assigned.Contains(slot) && this._retrievalKeys.Any(key => slot.Result.Text.Contains(key, StringComparison.Ordinal))))
		{
			throw UnresolvablePayloadException();
		}
		foreach (var (result, original) in restorations)
		{
			ToolResultRewriter.Rewrite(result, original);
		}
		return restorations.Count > 0;
	}

	private static PayloadAddress Describe(ToolResultRef result)
	{
		JsonObject? anchor = null;
		string? idProperty = null;
		for (JsonNode? current = result.Owner; current is not null; current = current.Parent)
		{
			if (current is not JsonObject candidate)
			{
				continue;
			}
			idProperty = ReadString(candidate["type"]) switch
			{
				"tool_result" => "tool_use_id",
				"function_call_output" => "call_id",
				_ => ReadString(candidate["role"]) == "tool" ? "tool_call_id" : null,
			};
			if (idProperty is not null)
			{
				anchor = candidate;
				break;
			}
		}

		var components = new List<string> { EscapePathPart(result.Key) };
		for (JsonNode? current = result.Owner; current is not null && !ReferenceEquals(current, anchor); current = current.Parent)
		{
			if (current.Parent is JsonArray array)
			{
				components.Add(array.IndexOf(current).ToString(System.Globalization.CultureInfo.InvariantCulture));
			}
			else if (current.Parent is JsonObject parent)
			{
				components.Add(EscapePathPart(parent.First(property => ReferenceEquals(property.Value, current)).Key));
			}
		}
		components.Reverse();
		return new PayloadAddress(idProperty, anchor is not null && idProperty is not null ? ReadString(anchor[idProperty]) : null, string.Join('/', components));
	}

	private static string EscapePathPart(string value) => value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

	private static JsonNode Root(JsonNode node)
	{
		while (node.Parent is not null)
		{
			node = node.Parent;
		}
		return node;
	}

	private static string? ReadString(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text) ? text : null;

	private CCRRequestState Copy()
	{
		var copy = new CCRRequestState
		{
			CreatedToolsProperty = this.CreatedToolsProperty,
			OwnsToolDefinition = this.OwnsToolDefinition,
			_originalDefinitions = this._originalDefinitions?.DeepClone() as JsonArray,
		};
		copy._retrievalKeys.UnionWith(this._retrievalKeys);
		copy._replayedRetrievalKeys.UnionWith(this._replayedRetrievalKeys);
		copy._payloadChanges.AddRange(this._payloadChanges.Select(change => new PayloadChange(change)));
		return copy;
	}

	private static JsonArray NamedDefinitions(JsonObject body, TopHatTarget target)
	{
		var definitions = new JsonArray();
		if (body["tools"] is JsonArray tools)
		{
			foreach (var tool in tools.OfType<JsonObject>())
			{
				var descriptor = target == TopHatTarget.OpenAIChatCompletions ? tool["function"] as JsonObject : tool;
				if (ReadString(descriptor?["name"]) == CCRToolDefinition.ToolName)
				{
					definitions.Add(tool.DeepClone());
				}
			}
		}
		return definitions;
	}

	private static HashSet<string> MetadataKeys(JsonObject body, TopHatTarget target)
	{
		var keys = new HashSet<string>(StringComparer.Ordinal);
		foreach (var result in ToolResultLocator.Find(body, target))
		{
			JsonNode? payload;
			try
			{
				payload = JsonNode.Parse(result.Text);
			}
			catch (JsonException)
			{
				continue;
			}
			var pending = new Stack<JsonNode>();
			if (payload is not null)
			{
				pending.Push(payload);
			}
			while (pending.TryPop(out var current))
			{
				if (current is JsonObject value)
				{
					if (value["_tophat_compression"] is JsonObject metadata && ReadString(metadata[CCRToolDefinition.RetrievalKeyField]) is string retrievalKey)
					{
						keys.Add(retrievalKey);
					}
					foreach (var child in value.Select(property => property.Value).OfType<JsonNode>())
					{
						pending.Push(child);
					}
				}
				else if (current is JsonArray array)
				{
					foreach (var child in array.OfType<JsonNode>())
					{
						pending.Push(child);
					}
				}
			}
		}
		return keys;
	}

	private bool RestoreDefinition(JsonObject body, TopHatTarget target)
	{
		if (this._originalDefinitions is null)
		{
			return CCRRequestPolicy.RemoveInternalDefinition(body, target, this.CreatedToolsProperty, ownsToolDefinition: true);
		}
		var tools = body["tools"]!.AsArray();
		var definitionIndex = 0;
		var changed = false;
		for (var index = 0; index < tools.Count; index++)
		{
			if (tools[index] is not JsonObject tool)
			{
				continue;
			}
			var descriptor = target == TopHatTarget.OpenAIChatCompletions ? tool["function"] as JsonObject : tool;
			if (ReadString(descriptor?["name"]) != CCRToolDefinition.ToolName)
			{
				continue;
			}
			if (definitionIndex >= this._originalDefinitions.Count)
			{
				throw new HttpRequestException("The number of replayed CCR definitions changed; their original contract cannot be restored safely.");
			}
			var original = this._originalDefinitions[definitionIndex++];
			if (!JsonNode.DeepEquals(tool, original))
			{
				// Replace only the private slot; preserve caller edits and its position relative to other tools.
				tools[index] = original!.DeepClone();
				changed = true;
			}
		}
		if (definitionIndex != this._originalDefinitions.Count)
		{
			throw new HttpRequestException("A replayed CCR definition disappeared; its original contract cannot be restored safely.");
		}
		return changed;
	}

	private static HttpRequestException UnresolvablePayloadException() => new("A later transform changed or ambiguously relocated a CCR-compressed payload after retrieval became unavailable; the request cannot be forwarded safely.");

	/// <summary>Encapsulates saved provenance so independently cloned retries cannot mutate another attempt's state.</summary>
	private sealed class RetrySnapshot
	{
		private readonly CCRRequestState _state;
		private readonly TopHatTarget _target;
		private readonly JsonArray _definitions;

		/// <summary>Captures the exact emitted definition and an independent copy of payload provenance.</summary>
		public RetrySnapshot(CCRRequestState state, TopHatTarget target, JsonArray definitions)
		{
			this._state = state.Copy();
			this._target = target;
			this._definitions = (JsonArray)definitions.DeepClone();
		}

		/// <summary>Rolls back only artifacts traceable to the prior attempt, rejecting changed owned content.</summary>
		public bool Restore(JsonObject body, TopHatTarget target)
		{
			var serialized = body.ToJsonString();
			var hasIssuedKeys = this._state._retrievalKeys.Any(key => serialized.Contains(key, StringComparison.Ordinal));
			var hasReplayedKeys = this._state._replayedRetrievalKeys.Count > 0 && MetadataKeys(body, this._target).Overlaps(this._state._replayedRetrievalKeys);
			var definitions = NamedDefinitions(body, this._target);
			var sameDefinition = JsonNode.DeepEquals(definitions, this._definitions);
			if (!hasIssuedKeys && !hasReplayedKeys && !sameDefinition)
			{
				// A copied option on an unrelated replacement request is not an ownership grant.
				return false;
			}
			if (target != this._target || (hasIssuedKeys && definitions.Count > 0 || hasReplayedKeys) && !sameDefinition)
			{
				throw new HttpRequestException("A retry changed the provider or an owned CCR tool definition while private artifacts remain; it cannot be forwarded safely.");
			}

			var attempt = this._state.Copy();
			var hasRecordedSlots = ToolResultLocator.Find(body, target).Any(result =>
				attempt._payloadChanges.Any(change => change.Address == Describe(result)));
			var changed = (hasIssuedKeys || hasRecordedSlots) && attempt.RestorePayloads(body, target);
			if (sameDefinition)
			{
				changed |= attempt.RestoreDefinition(body, target);
			}
			return changed;
		}
	}

	private readonly record struct PayloadAddress(string? IdProperty, string? CallId, string Path);

	private sealed record PayloadSlot(ToolResultRef Result, PayloadAddress Address);

	private sealed class PayloadChange
	{
		/// <summary>Copies immutable source identity and independently owns all mutable version tracking.</summary>
		public PayloadChange(PayloadChange source)
		{
			this.Owner = source.Owner;
			this.Key = source.Key;
			this.Address = source.Address;
			this.Original = source.Original;
			this.CanRestore = source.CanRestore;
			this.Versions.UnionWith(source.Versions);
		}

		/// <summary>Captures a result slot before its first compression change.</summary>
		public PayloadChange(ToolResultRef result, PayloadAddress address)
		{
			this.Owner = result.Owner;
			this.Key = result.Key;
			this.Address = address;
			this.Original = result.Text;
		}

		/// <summary>The original live owner, used to distinguish repeated passes from other slots.</summary>
		public JsonObject Owner { get; }

		/// <summary>The payload field within the owner.</summary>
		public string Key { get; }

		/// <summary>The provider call identifier and relative content path.</summary>
		public PayloadAddress Address { get; }

		/// <summary>The complete payload before any compression pass.</summary>
		public string Original { get; }

		/// <summary>Every compressed representation emitted for this slot.</summary>
		public HashSet<string> Versions { get; } = new(StringComparer.Ordinal);

		/// <summary>Whether no intervening caller transform changed this slot between compression passes.</summary>
		public bool CanRestore { get; set; } = true;

		/// <summary>Checks whether the payload is an unchanged original or a known compressed version.</summary>
		public bool Matches(string value) => this.Original == value || this.Versions.Contains(value);
	}
}
