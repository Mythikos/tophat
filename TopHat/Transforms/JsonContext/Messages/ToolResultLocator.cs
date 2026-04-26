using System.Text.Json.Nodes;
using TopHat.Providers;

namespace TopHat.Transforms.JsonContext.Messages;

/// <summary>
/// Finds all tool-result string slots in a parsed request body across the three
/// wire formats TopHat supports. Returns <see cref="ToolResultRef"/> instances
/// that <see cref="ToolResultRewriter"/> can update in place.
/// </summary>
internal static class ToolResultLocator
{
	/// <summary>
	/// Locates all tool-result strings in <paramref name="body"/>, skipping the first
	/// <paramref name="frozenMessageCount"/> messages (which are assumed to be in the
	/// provider's prefix cache and must not be mutated).
	/// </summary>
	public static IReadOnlyList<ToolResultRef> Find(JsonObject body, TopHatTarget target, int frozenMessageCount = 0)
	{
		return target switch
		{
			TopHatTarget.AnthropicMessages => FindAnthropicToolResults(body, frozenMessageCount),
			TopHatTarget.OpenAIChatCompletions => FindOpenAiChatToolResults(body, frozenMessageCount),
			TopHatTarget.OpenAIResponses => FindOpenAiResponsesToolResults(body, frozenMessageCount),
			_ => [],
		};
	}

	#region Anthropic /v1/messages

	private static List<ToolResultRef> FindAnthropicToolResults(JsonObject body, int frozenMessageCount)
	{
		var results = new List<ToolResultRef>();

		if (body["messages"] is not JsonArray messages)
		{
			return results;
		}

		for (var msgIdx = frozenMessageCount; msgIdx < messages.Count; msgIdx++)
		{
			if (messages[msgIdx] is not JsonObject msg)
			{
				continue;
			}

			if (msg["content"] is not JsonArray contentArr)
			{
				continue;
			}

			foreach (var block in contentArr)
			{
				if (block is not JsonObject blockObj)
				{
					continue;
				}

				if (blockObj["type"] is not JsonValue typeVal || !typeVal.TryGetValue<string>(out var blockType) || blockType != "tool_result")
				{
					continue;
				}

				var toolContent = blockObj["content"];

				// String form: content is directly a string.
				if (toolContent is JsonValue sv && sv.TryGetValue<string>(out var text))
				{
					results.Add(new ToolResultRef { Owner = blockObj, Key = "content", Text = text });
					continue;
				}

				// Array form: content is an array of { "type": "text", "text": "..." } blocks.
				if (toolContent is JsonArray textBlocks)
				{
					foreach (var tb in textBlocks)
					{
						if (tb is not JsonObject tbObj)
						{
							continue;
						}

						if (tbObj["type"] is not JsonValue tbTypeVal || !tbTypeVal.TryGetValue<string>(out var tbType) || tbType != "text")
						{
							continue;
						}

						if (tbObj["text"] is JsonValue textVal && textVal.TryGetValue<string>(out var textStr))
						{
							results.Add(new ToolResultRef { Owner = tbObj, Key = "text", Text = textStr });
						}
					}
				}
			}
		}

		return results;
	}

	#endregion

	#region OpenAI /v1/chat/completions

	private static List<ToolResultRef> FindOpenAiChatToolResults(JsonObject body, int frozenMessageCount)
	{
		var results = new List<ToolResultRef>();

		if (body["messages"] is not JsonArray messages)
		{
			return results;
		}

		for (var msgIdx = frozenMessageCount; msgIdx < messages.Count; msgIdx++)
		{
			if (messages[msgIdx] is not JsonObject msg)
			{
				continue;
			}

			if (msg["role"] is not JsonValue roleVal || !roleVal.TryGetValue<string>(out var role) || role != "tool")
			{
				continue;
			}

			if (msg["content"] is JsonValue cv && cv.TryGetValue<string>(out var content))
			{
				results.Add(new ToolResultRef { Owner = msg, Key = "content", Text = content });
			}
		}

		return results;
	}

	#endregion

	#region OpenAI /v1/responses

	private static List<ToolResultRef> FindOpenAiResponsesToolResults(JsonObject body, int frozenMessageCount)
	{
		var results = new List<ToolResultRef>();

		// input may be a string (no tool results possible) or an array.
		if (body["input"] is not JsonArray input)
		{
			return results;
		}

		// The responses API does not have a direct message-index equivalent to frozenMessageCount;
		// skip the first frozenMessageCount items as a best-effort approximation.
		var startIdx = Math.Min(frozenMessageCount, input.Count);

		for (var idx = startIdx; idx < input.Count; idx++)
		{
			if (input[idx] is not JsonObject item)
			{
				continue;
			}

			if (item["type"] is not JsonValue typeVal || !typeVal.TryGetValue<string>(out var itemType) || itemType != "function_call_output")
			{
				continue;
			}

			if (item["output"] is JsonValue outVal && outVal.TryGetValue<string>(out var output))
			{
				results.Add(new ToolResultRef { Owner = item, Key = "output", Text = output });
			}
		}

		return results;
	}

	#endregion
}
