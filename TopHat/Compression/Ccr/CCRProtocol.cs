using System.Text.Json;
using System.Text.Json.Nodes;
using TopHat.Providers;

namespace TopHat.Compression.CCR;

/// <summary>Encodes the provider-specific tool transcript and terminal-response shapes.</summary>
internal sealed class CCRProtocol
{
	private readonly TopHatTarget _target;

	/// <summary>Creates the codec for one provider endpoint.</summary>
	public CCRProtocol(TopHatTarget target)
	{
		this._target = target;
	}

	/// <summary>Reports whether a local conversation array can accept the next retrieval turn.</summary>
	public bool CanContinue(JsonObject request) => request[this._target == TopHatTarget.OpenAIResponses ? "input" : "messages"] is JsonArray;

	/// <summary>Classifies every response choice while retaining the provider's original call objects.</summary>
	public CCRResponseCalls Inspect(JsonObject response)
	{
		var retrievals = new List<JsonObject>();
		var hasForeign = false;
		var multipleChoices = this._target == TopHatTarget.OpenAIChatCompletions && response["choices"] is JsonArray choices && choices.Count > 1;
		var invalidEnvelope = this._target == TopHatTarget.OpenAIChatCompletions && (response["choices"] is not JsonArray chatChoices || chatChoices.Count == 0 || chatChoices[0] is not JsonObject firstChoice || firstChoice["message"] is not JsonObject);
		foreach (var calls in this.CallArrays(response))
		{
			foreach (var call in calls.OfType<JsonObject>())
			{
				if (!this.IsCall(call))
				{
					continue;
				}
				if (this.IsRetrieval(call))
				{
					retrievals.Add(call);
				}
				else
				{
					hasForeign = true;
				}
			}
		}
		if (this._target == TopHatTarget.OpenAIChatCompletions && response["choices"] is JsonArray legacyChoices)
		{
			foreach (var choice in legacyChoices.OfType<JsonObject>())
			{
				if (choice["message"] is JsonObject message && message["function_call"] is JsonObject legacyCall)
				{
					if (String(legacyCall["name"]) == CCRToolDefinition.ToolName)
					{
						// Legacy calls have no tool-call ID and cannot be paired with a modern tool result.
						retrievals.Add(legacyCall);
						invalidEnvelope = true;
					}
					else
					{
						hasForeign = true;
					}
				}
			}
		}
		return new CCRResponseCalls(retrievals, hasForeign, multipleChoices, invalidEnvelope);
	}

	/// <summary>Removes private calls and, with request ownership evidence, private echoed configuration.</summary>
	public bool Sanitize(JsonObject response, bool advertisedInternalTool = false)
	{
		var changed = false;
		if (this._target == TopHatTarget.OpenAIChatCompletions && response["choices"] is JsonArray choices)
		{
			foreach (var choice in choices.OfType<JsonObject>())
			{
				if (choice["message"] is not JsonObject message)
				{
					continue;
				}
				var removedModern = message["tool_calls"] is JsonArray calls && this.RemoveRetrievalCalls(calls);
				var removedLegacy = message["function_call"] is JsonObject legacyCall && String(legacyCall["name"]) == CCRToolDefinition.ToolName;
				if (!removedModern && !removedLegacy)
				{
					continue;
				}
				changed = true;
				if (removedLegacy)
				{
					message.Remove("function_call");
				}
				if (removedModern && message["tool_calls"] is JsonArray remaining && remaining.Count == 0)
				{
					message.Remove("tool_calls");
				}
				if (String(choice["finish_reason"]) is "tool_calls" or "function_call")
				{
					choice["finish_reason"] = message["tool_calls"] is JsonArray remainingCalls && remainingCalls.Count > 0 ? "tool_calls" : message["function_call"] is JsonObject ? "function_call" : "stop";
				}
			}
		}
		else
		{
			foreach (var calls in this.CallArrays(response))
			{
				changed |= this.RemoveRetrievalCalls(calls);
			}
			if (changed && this._target == TopHatTarget.AnthropicMessages && !this.Inspect(response).HasForeign && String(response["stop_reason"]) == "tool_use")
			{
				response["stop_reason"] = "end_turn";
			}
		}
		if (advertisedInternalTool)
		{
			changed |= this.RemoveEchoedConfiguration(response);
		}
		return changed;
	}

	/// <summary>Parses a retrieval's arguments, treating malformed arguments as an unavailable retrieval.</summary>
	public JsonObject? Arguments(JsonObject call)
	{
		if (this._target == TopHatTarget.AnthropicMessages)
		{
			return call["input"] as JsonObject;
		}
		var arguments = String(this._target == TopHatTarget.OpenAIChatCompletions ? (call["function"] as JsonObject)?["arguments"] : call["arguments"]);
		if (string.IsNullOrEmpty(arguments))
		{
			return null;
		}
		try
		{
			return JsonNode.Parse(arguments) as JsonObject;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	/// <summary>Rejects calls that cannot be paired unambiguously with provider tool results.</summary>
	public bool HasValidCallIds(IReadOnlyList<JsonObject> calls)
	{
		var seen = new HashSet<string>(StringComparer.Ordinal);
		return calls.All(call => this.CallId(call) is { Length: > 0 } id && seen.Add(id));
	}

	/// <summary>Remaps only newly emitted internal IDs that collide with history, preserving each new call and its eventual result.</summary>
	public void NormalizeCallIds(JsonObject request, JsonObject response, IReadOnlyList<JsonObject> calls)
	{
		var usedCallIds = new HashSet<string>(StringComparer.Ordinal);
		var usedItemIds = new HashSet<string>(StringComparer.Ordinal);
		foreach (var item in ((JsonArray)request[this._target == TopHatTarget.OpenAIResponses ? "input" : "messages"]!).OfType<JsonObject>())
		{
			if (this._target == TopHatTarget.AnthropicMessages && item["content"] is JsonArray content)
			{
				foreach (var block in content.OfType<JsonObject>())
				{
					AddId(usedCallIds, String(block["type"]) == "tool_result" ? block["tool_use_id"] : block["id"]);
				}
			}
			else if (this._target == TopHatTarget.OpenAIChatCompletions)
			{
				AddId(usedCallIds, item["tool_call_id"]);
				if (item["tool_calls"] is JsonArray toolCalls)
				{
					foreach (var call in toolCalls.OfType<JsonObject>())
					{
						AddId(usedCallIds, call["id"]);
					}
				}
			}
			else if (this._target == TopHatTarget.OpenAIResponses)
			{
				AddId(usedCallIds, item["call_id"]);
				AddId(usedItemIds, item["id"]);
			}
		}

		var reservedCallIds = calls.Select(this.CallId).OfType<string>().ToHashSet(StringComparer.Ordinal);
		var reservedItemIds = response["output"] is JsonArray output
			? output.OfType<JsonObject>().Select(item => String(item["id"])).OfType<string>().ToHashSet(StringComparer.Ordinal)
			: new HashSet<string>(StringComparer.Ordinal);
		foreach (var call in calls)
		{
			if (!usedCallIds.Add(this.CallId(call)!))
			{
				call[this._target == TopHatTarget.OpenAIResponses ? "call_id" : "id"] = UniqueId(usedCallIds, reservedCallIds, this._target == TopHatTarget.AnthropicMessages ? "toolu_tophat_" : "call_tophat_");
			}
			if (this._target == TopHatTarget.OpenAIResponses && String(call["id"]) is { Length: > 0 } itemId && !usedItemIds.Add(itemId))
			{
				call["id"] = UniqueId(usedItemIds, reservedItemIds, "fc_");
			}
		}
	}

	/// <summary>Creates a tool result paired with the call's effective identifier after collision normalization.</summary>
	public JsonObject Result(JsonObject call, string content)
	{
		return this._target switch
		{
			TopHatTarget.AnthropicMessages => new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = this.CallId(call), ["content"] = content },
			TopHatTarget.OpenAIChatCompletions => new JsonObject { ["role"] = "tool", ["tool_call_id"] = this.CallId(call), ["content"] = content },
			_ => new JsonObject { ["type"] = "function_call_output", ["call_id"] = this.CallId(call), ["output"] = content },
		};
	}

	/// <summary>Appends the complete assistant output and its paired internal results.</summary>
	public void AppendTurn(JsonObject request, JsonObject response, IReadOnlyList<JsonObject> results)
	{
		var transcript = (JsonArray)request[this._target == TopHatTarget.OpenAIResponses ? "input" : "messages"]!;
		if (this._target == TopHatTarget.AnthropicMessages)
		{
			transcript.Add(new JsonObject { ["role"] = "assistant", ["content"] = response["content"]!.DeepClone() });
			transcript.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(results.Select(result => (JsonNode)result).ToArray()) });
		}
		else if (this._target == TopHatTarget.OpenAIChatCompletions)
		{
			transcript.Add(response["choices"]![0]!["message"]!.DeepClone());
			foreach (var result in results)
			{
				transcript.Add(result);
			}
		}
		else
		{
			// Reasoning and message items are part of the provider's continuation state, not just function calls.
			foreach (var item in (JsonArray)response["output"]!)
			{
				transcript.Add(item?.DeepClone());
			}
			foreach (var result in results)
			{
				transcript.Add(result);
			}
		}
	}

	/// <summary>Withdraws the private tool before the final exhaustion completion without disabling caller tools.</summary>
	public void DisableRetrieval(JsonObject request)
	{
		var noToolsRemain = false;
		if (request["tools"] is JsonArray tools)
		{
			for (var index = tools.Count - 1; index >= 0; index--)
			{
				if (tools[index] is JsonObject tool && this.IsPrivateDefinitionEcho(tool))
				{
					tools.RemoveAt(index);
				}
			}
			noToolsRemain = tools.Count == 0;
		}
		CCRRequestPolicy.RemoveInternalToolSelection(request, this._target, noToolsRemain);
	}

	/// <summary>Reads a string without throwing for malformed provider fields.</summary>
	public static string? String(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;

	private bool RemoveRetrievalCalls(JsonArray calls)
	{
		var changed = false;
		for (var index = calls.Count - 1; index >= 0; index--)
		{
			if (calls[index] is JsonObject call && this.IsRetrieval(call))
			{
				calls.RemoveAt(index);
				changed = true;
			}
		}
		return changed;
	}

	private bool RemoveEchoedConfiguration(JsonObject response)
	{
		var changed = false;
		var noToolsRemain = false;
		if (response["tools"] is JsonArray tools)
		{
			for (var index = tools.Count - 1; index >= 0; index--)
			{
				if (tools[index] is JsonObject tool && this.IsPrivateDefinitionEcho(tool))
				{
					tools.RemoveAt(index);
					changed = true;
				}
			}
			noToolsRemain = changed && tools.Count == 0;
		}
		changed |= CCRRequestPolicy.RemoveInternalToolSelection(response, this._target, noToolsRemain);
		return changed;
	}

	private bool IsPrivateDefinitionEcho(JsonObject tool)
	{
		if (this._target == TopHatTarget.AnthropicMessages)
		{
			return String(tool["name"]) == CCRToolDefinition.ToolName && String(tool["type"]) is null or "custom";
		}
		return String(tool["type"]) == "function" && String(this._target == TopHatTarget.OpenAIChatCompletions ? (tool["function"] as JsonObject)?["name"] : tool["name"]) == CCRToolDefinition.ToolName;
	}

	private static void AddId(HashSet<string> identifiers, JsonNode? node)
	{
		if (String(node) is { Length: > 0 } id)
		{
			identifiers.Add(id);
		}
	}

	private static string UniqueId(HashSet<string> used, HashSet<string> reserved, string prefix)
	{
		string id;
		do
		{
			id = prefix + Guid.NewGuid().ToString("N");
		}
		while (reserved.Contains(id) || !used.Add(id));
		return id;
	}

	private string? CallId(JsonObject call) => String(call[this._target == TopHatTarget.OpenAIResponses ? "call_id" : "id"]);

	private bool IsCall(JsonObject call) => this._target switch
	{
		TopHatTarget.AnthropicMessages => String(call["type"]) == "tool_use",
		TopHatTarget.OpenAIChatCompletions => true,
		_ => String(call["type"]) is "function_call" or "custom_tool_call",
	};

	private bool IsRetrieval(JsonObject call)
	{
		if (!this.IsCall(call) || (this._target == TopHatTarget.OpenAIResponses && String(call["type"]) != "function_call") || (this._target == TopHatTarget.OpenAIChatCompletions && String(call["type"]) != "function"))
		{
			return false;
		}
		return String(this._target == TopHatTarget.OpenAIChatCompletions ? (call["function"] as JsonObject)?["name"] : call["name"]) == CCRToolDefinition.ToolName;
	}

	private IEnumerable<JsonArray> CallArrays(JsonObject response)
	{
		if (this._target == TopHatTarget.OpenAIChatCompletions)
		{
			if (response["choices"] is JsonArray choices)
			{
				foreach (var choice in choices.OfType<JsonObject>())
				{
					if (choice["message"] is JsonObject message && message["tool_calls"] is JsonArray calls)
					{
						yield return calls;
					}
				}
			}
		}
		else if (response[this._target == TopHatTarget.AnthropicMessages ? "content" : "output"] is JsonArray calls)
		{
			yield return calls;
		}
	}
}

/// <summary>Classified call ownership and branching information for one provider response.</summary>
internal sealed record CCRResponseCalls(IReadOnlyList<JsonObject> Retrievals, bool HasForeign, bool HasMultipleChoices, bool HasInvalidEnvelope);
