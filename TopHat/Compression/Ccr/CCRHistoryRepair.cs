using System.Text.Json.Nodes;
using TopHat.Providers;

namespace TopHat.Compression.CCR;

/// <summary>Repairs identifiable internal retrieval history without claiming ownership of caller tool results.</summary>
internal static class CCRHistoryRepair
{
	private const string UnavailableResult = "TopHat repaired an unanswered internal retrieval call. Its original result is unavailable; continue with the information already present.";

	/// <summary>Normalizes internal call/result pairs and returns whether the request body changed.</summary>
	public static bool Repair(JsonObject body, TopHatTarget target)
	{
		ArgumentNullException.ThrowIfNull(body);
		if (target is not (TopHatTarget.AnthropicMessages or TopHatTarget.OpenAIChatCompletions or TopHatTarget.OpenAIResponses))
		{
			return false;
		}

		var property = target == TopHatTarget.OpenAIResponses ? "input" : "messages";
		if (body[property] is not JsonArray original)
		{
			return false;
		}
		if (!FindCalls(original, target).Any(call => IsInternal(call, target)))
		{
			return false;
		}

		var transcript = (JsonArray)original.DeepClone();
		var calls = FindCalls(transcript, target).ToArray();
		var callerIds = calls.Where(call => !IsInternal(call, target)).Select(call => CallId(call, target)).OfType<string>().ToHashSet(StringComparer.Ordinal);
		var ownedIds = calls.Where(call => IsInternal(call, target)).Select(call => CallId(call, target)).OfType<string>().Where(id => !callerIds.Contains(id)).ToHashSet(StringComparer.Ordinal);
		var retainedIds = new HashSet<string>(StringComparer.Ordinal);
		var retainedCalls = new List<JsonObject>();
		foreach (var call in calls)
		{
			if (!IsInternal(call, target))
			{
				continue;
			}

			var id = CallId(call, target);
			if (string.IsNullOrWhiteSpace(id) || callerIds.Contains(id) || !retainedIds.Add(id))
			{
				RemoveItem(call, transcript, target);
				continue;
			}

			retainedCalls.Add(call);
		}

		// The original named calls prove ownership even when malformed calls were removed above.
		// An otherwise unknown result ID (including an internal-looking prefix) proves nothing.
		foreach (var result in FindResults(transcript, target).ToArray())
		{
			var id = ResultId(result, target);
			if (id is not null && ownedIds.Contains(id) && !retainedIds.Contains(id))
			{
				RemoveItem(result, transcript, target);
			}
		}

		foreach (var call in retainedCalls)
		{
			var id = CallId(call, target)!;
			var results = FindResults(transcript, target).Where(result => ResultId(result, target) == id).ToArray();
			var retained = results.FirstOrDefault(result => IsCorrectlyPlaced(call, result, transcript, target)) ?? results.FirstOrDefault();
			foreach (var result in results)
			{
				if (!ReferenceEquals(result, retained))
				{
					RemoveItem(result, transcript, target);
				}
			}

			if (retained is not null && IsCorrectlyPlaced(call, retained, transcript, target))
			{
				continue;
			}

			if (retained is not null)
			{
				RemoveItem(retained, transcript, target);
			}
			InsertResult(call, retained ?? CreateUnavailableResult(id, target), transcript, target);
		}

		if (JsonNode.DeepEquals(original, transcript))
		{
			return false;
		}

		body[property] = transcript;
		return true;
	}

	private static IEnumerable<JsonObject> FindCalls(JsonArray transcript, TopHatTarget target)
	{
		foreach (var item in transcript.OfType<JsonObject>())
		{
			if (target == TopHatTarget.OpenAIResponses)
			{
				if (ReadString(item["type"]) == "function_call")
				{
					yield return item;
				}
				continue;
			}

			if (ReadString(item["role"]) != "assistant")
			{
				continue;
			}
			var property = target == TopHatTarget.AnthropicMessages ? "content" : "tool_calls";
			if (item[property] is not JsonArray children)
			{
				continue;
			}
			foreach (var child in children.OfType<JsonObject>())
			{
				if (target == TopHatTarget.OpenAIChatCompletions || ReadString(child["type"]) == "tool_use")
				{
					yield return child;
				}
			}
		}
	}

	private static IEnumerable<JsonObject> FindResults(JsonArray transcript, TopHatTarget target)
	{
		foreach (var item in transcript.OfType<JsonObject>())
		{
			if (target == TopHatTarget.AnthropicMessages)
			{
				if (item["content"] is JsonArray content)
				{
					foreach (var block in content.OfType<JsonObject>())
					{
						if (ReadString(block["type"]) == "tool_result")
						{
							yield return block;
						}
					}
				}
			}
			else if ((target == TopHatTarget.OpenAIChatCompletions && ReadString(item["role"]) == "tool")
				|| (target == TopHatTarget.OpenAIResponses && ReadString(item["type"]) == "function_call_output"))
			{
				yield return item;
			}
		}
	}

	private static bool IsCorrectlyPlaced(JsonObject call, JsonObject result, JsonArray transcript, TopHatTarget target)
	{
		if (target == TopHatTarget.OpenAIResponses)
		{
			return transcript.IndexOf(result) > transcript.IndexOf(call);
		}

		var assistant = (JsonObject)call.Parent!.Parent!;
		var assistantIndex = transcript.IndexOf(assistant);
		if (target == TopHatTarget.AnthropicMessages)
		{
			if (result.Parent is not JsonArray content || content.Parent is not JsonObject user || ReadString(user["role"]) != "user"
				|| transcript.IndexOf(user) != assistantIndex + 1)
			{
				return false;
			}

			return content.Take(content.IndexOf(result)).All(block => block is JsonObject blockObject && ReadString(blockObject["type"]) == "tool_result");
		}

		var resultIndex = transcript.IndexOf(result);
		return resultIndex > assistantIndex && transcript.Skip(assistantIndex + 1).Take(resultIndex - assistantIndex)
			.All(message => message is JsonObject messageObject && ReadString(messageObject["role"]) == "tool");
	}

	private static void InsertResult(JsonObject call, JsonObject result, JsonArray transcript, TopHatTarget target)
	{
		if (target == TopHatTarget.OpenAIResponses)
		{
			transcript.Insert(transcript.IndexOf(call) + 1, result);
			return;
		}

		var assistant = (JsonObject)call.Parent!.Parent!;
		var insertionIndex = transcript.IndexOf(assistant) + 1;
		if (target == TopHatTarget.OpenAIChatCompletions)
		{
			while (insertionIndex < transcript.Count && transcript[insertionIndex] is JsonObject message && ReadString(message["role"]) == "tool")
			{
				insertionIndex++;
			}
			transcript.Insert(insertionIndex, result);
			return;
		}

		JsonObject user;
		if (insertionIndex < transcript.Count && transcript[insertionIndex] is JsonObject next && ReadString(next["role"]) == "user"
			&& (next["content"] is JsonArray || ReadString(next["content"]) is not null || next["content"] is null))
		{
			user = next;
		}
		else
		{
			user = new JsonObject { ["role"] = "user", ["content"] = new JsonArray() };
			transcript.Insert(insertionIndex, user);
		}

		if (user["content"] is not JsonArray content)
		{
			content = new JsonArray();
			if (ReadString(user["content"]) is { Length: > 0 } text)
			{
				content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
			}
			user["content"] = content;
		}

		var contentIndex = 0;
		while (contentIndex < content.Count && content[contentIndex] is JsonObject block && ReadString(block["type"]) == "tool_result")
		{
			contentIndex++;
		}
		content.Insert(contentIndex, result);
	}

	private static void RemoveItem(JsonObject item, JsonArray transcript, TopHatTarget target)
	{
		if (item.Parent is not JsonArray parent)
		{
			return;
		}

		var message = parent.Parent as JsonObject;
		parent.Remove(item);
		if (message is null)
		{
			return;
		}

		if ((target == TopHatTarget.AnthropicMessages && parent.Count == 0)
			|| (target == TopHatTarget.OpenAIChatCompletions && parent.Count == 0 && !HasContent(message)))
		{
			transcript.Remove(message);
		}
		else if (target == TopHatTarget.OpenAIChatCompletions && parent.Count == 0)
		{
			message.Remove("tool_calls");
		}
	}

	private static bool HasContent(JsonObject message) => message["content"] switch
	{
		JsonArray array => array.Count > 0,
		JsonValue value => value.TryGetValue<string>(out var text) && text.Length > 0,
		_ => message["refusal"] is not null || message["audio"] is not null,
	};

	private static JsonObject CreateUnavailableResult(string id, TopHatTarget target) => target switch
	{
		TopHatTarget.AnthropicMessages => new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = UnavailableResult, ["is_error"] = true },
		TopHatTarget.OpenAIChatCompletions => new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = UnavailableResult },
		_ => new JsonObject { ["type"] = "function_call_output", ["call_id"] = id, ["output"] = UnavailableResult },
	};

	private static bool IsInternal(JsonObject call, TopHatTarget target) => target == TopHatTarget.OpenAIChatCompletions
		? ReadString(call["type"]) == "function" && call["function"] is JsonObject function && ReadString(function["name"]) == CCRToolDefinition.ToolName
		: ReadString(call["name"]) == CCRToolDefinition.ToolName;

	private static string? CallId(JsonObject call, TopHatTarget target) => ReadString(call[target == TopHatTarget.OpenAIResponses ? "call_id" : "id"]);

	private static string? ResultId(JsonObject result, TopHatTarget target) => ReadString(result[target switch
	{
		TopHatTarget.AnthropicMessages => "tool_use_id",
		TopHatTarget.OpenAIChatCompletions => "tool_call_id",
		_ => "call_id",
	}]);

	private static string? ReadString(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
