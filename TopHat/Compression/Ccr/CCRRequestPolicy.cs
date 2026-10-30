using System.Text.Json.Nodes;
using TopHat.Providers;

namespace TopHat.Compression.CCR;

/// <summary>Defines request modes in which internal retrieval can be completed transparently.</summary>
internal static class CCRRequestPolicy
{
	/// <summary>Checks the live body before retrieval-dependent compression or dispatch.</summary>
	public static bool CanRetrieve(JsonObject body, TopHatTarget target, bool ownsToolDefinition = false)
	{
		if (IsTrue(body["stream"]) || HasCallerToolCollision(body, target, ownsToolDefinition) || !AllowsRetrievalChoice(body, target))
		{
			return false;
		}
		return target switch
		{
			TopHatTarget.AnthropicMessages => body["messages"] is JsonArray,
			// Legacy function selection does not control the injected modern tool contract.
			TopHatTarget.OpenAIChatCompletions => body["messages"] is JsonArray && body["functions"] is null && body["function_call"] is null
				&& (body["n"] is null || body["n"] is JsonValue count && count.TryGetValue<int>(out var candidates) && candidates == 1),
			// Stored responses retain the original output on the server, including calls removed locally.
			// Stateless requests keep the complete, repairable conversation in the caller's input.
			TopHatTarget.OpenAIResponses => body["input"] is JsonArray && IsFalse(body["store"]) && !IsTrue(body["background"]) && body["previous_response_id"] is null && body["conversation"] is null,
			_ => false,
		};
	}

	/// <summary>Distinguishes the canonical injected definition from a caller-owned same-name tool.</summary>
	public static bool HasCallerToolCollision(JsonObject body, TopHatTarget target, bool ownsToolDefinition = false)
	{
		return body["tools"] is JsonArray tools && tools.OfType<JsonObject>().Any(tool => HasRetrievalName(tool, target) && !IsInternalDefinition(tool, target, ownsToolDefinition))
			|| target == TopHatTarget.OpenAIChatCompletions && body["functions"] is JsonArray functions && functions.OfType<JsonObject>().Any(function => CCRProtocol.String(function["name"]) == CCRToolDefinition.ToolName);
	}

	/// <summary>Checks whether the request still advertises TopHat's own tool definition.</summary>
	public static bool HasInternalDefinition(JsonObject body, TopHatTarget target, bool ownsToolDefinition = false) => body["tools"] is JsonArray tools && tools.OfType<JsonObject>().Any(tool => IsInternalDefinition(tool, target, ownsToolDefinition));

	/// <summary>Removes only canonical internal definitions when retrieval is unavailable.</summary>
	public static bool RemoveInternalDefinition(JsonObject body, TopHatTarget target, bool removeEmptyArray, bool ownsToolDefinition = false)
	{
		if (body["tools"] is not JsonArray tools)
		{
			return false;
		}
		var changed = false;
		for (var index = tools.Count - 1; index >= 0; index--)
		{
			if (tools[index] is JsonObject tool && IsInternalDefinition(tool, target, ownsToolDefinition))
			{
				tools.RemoveAt(index);
				changed = true;
			}
		}
		if (changed && removeEmptyArray && tools.Count == 0)
		{
			body.Remove("tools");
		}
		if (changed)
		{
			RemoveInternalToolSelection(body, target, tools.Count == 0);
		}
		return changed;
	}

	/// <summary>Withdraws selectors for a proven private tool while preserving caller function, custom, and hosted-tool selections.</summary>
	public static bool RemoveInternalToolSelection(JsonObject body, TopHatTarget target, bool noToolsRemain = false)
	{
		if (body["tool_choice"] is JsonObject choice)
		{
			if (IsInternalToolSelection(choice, target) || noToolsRemain && target == TopHatTarget.AnthropicMessages && CCRProtocol.String(choice["type"]) == "any")
			{
				body.Remove("tool_choice");
				return true;
			}
			if (target == TopHatTarget.OpenAIResponses && CCRProtocol.String(choice["type"]) == "allowed_tools" && choice["tools"] is JsonArray allowedTools)
			{
				var changed = false;
				for (var index = allowedTools.Count - 1; index >= 0; index--)
				{
					if (allowedTools[index] is JsonObject selection && IsInternalToolSelection(selection, target))
					{
						allowedTools.RemoveAt(index);
						changed = true;
					}
				}
				if (changed && allowedTools.Count == 0)
				{
					body.Remove("tool_choice");
				}
				return changed;
			}
		}
		else if (noToolsRemain && CCRProtocol.String(body["tool_choice"]) == "required")
		{
			body.Remove("tool_choice");
			return true;
		}
		return false;
	}

	private static bool HasRetrievalName(JsonObject tool, TopHatTarget target)
	{
		var descriptor = target == TopHatTarget.OpenAIChatCompletions ? tool["function"] as JsonObject : tool;
		return descriptor?["name"] is JsonValue name && name.TryGetValue<string>(out var text) && text == CCRToolDefinition.ToolName;
	}

	private static bool AllowsRetrievalChoice(JsonObject body, TopHatTarget target)
	{
		var choice = body["tool_choice"];
		if (choice is null)
		{
			return true;
		}
		if (choice is JsonValue scalar && scalar.TryGetValue<string>(out var mode))
		{
			return mode is "auto" or "required";
		}
		if (choice is JsonObject selection)
		{
			var type = selection["type"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
			if (target == TopHatTarget.AnthropicMessages && type is "auto" or "any")
			{
				return true;
			}
			return IsInternalToolSelection(selection, target);
		}
		return false;
	}

	private static bool IsInternalToolSelection(JsonObject selection, TopHatTarget target)
	{
		return CCRProtocol.String(selection["type"]) == (target == TopHatTarget.AnthropicMessages ? "tool" : "function") && HasRetrievalName(selection, target);
	}

	private static bool IsInternalDefinition(JsonObject tool, TopHatTarget target, bool ownsToolDefinition = false)
	{
		var canonical = target switch
		{
			TopHatTarget.AnthropicMessages => CCRToolDefinition.BuildAnthropicToolDefinition(),
			TopHatTarget.OpenAIChatCompletions => CCRToolDefinition.BuildOpenAIChatCompletionsToolDefinition(),
			TopHatTarget.OpenAIResponses => CCRToolDefinition.BuildOpenAIResponsesToolDefinition(),
			_ => null,
		};
		return canonical is not null && (JsonNode.DeepEquals(tool, canonical) || ownsToolDefinition && MatchesWithDecorations(tool, canonical));
	}

	private static bool MatchesWithDecorations(JsonNode? actual, JsonNode? expected)
	{
		if (actual is JsonObject actualObject && expected is JsonObject expectedObject)
		{
			return expectedObject.All(property => actualObject.ContainsKey(property.Key) && MatchesWithDecorations(actualObject[property.Key], property.Value))
				&& actualObject.All(property => expectedObject.ContainsKey(property.Key) || property.Key is "cache_control" or "strict");
		}
		return JsonNode.DeepEquals(actual, expected);
	}

	private static bool IsTrue(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

	private static bool IsFalse(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && !flag;
}
