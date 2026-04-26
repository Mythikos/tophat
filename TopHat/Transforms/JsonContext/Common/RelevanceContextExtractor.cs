using System.Text.Json.Nodes;
using TopHat.Providers;

namespace TopHat.Transforms.JsonContext.Common;

/// <summary>
/// Extracts a relevance query context string from recent conversation messages.
/// Port of headroom's SmartCrusher._extract_context_from_messages.
/// </summary>
internal static class RelevanceContextExtractor
{
	private const int MaxUserMessages = 5;

	/// <summary>
	/// Builds a context string from the last <see cref="MaxUserMessages"/> user messages
	/// and any assistant tool-call arguments found in the message array.
	/// Returns empty string if no useful context is found.
	/// </summary>
	public static string Extract(JsonObject body, TopHatTarget target)
	{
		return target switch
		{
			TopHatTarget.AnthropicMessages => ExtractFromAnthropicMessages(body),
			TopHatTarget.OpenAIChatCompletions => ExtractFromOpenAiMessages(body),
			TopHatTarget.OpenAIResponses => ExtractFromOpenAiInput(body),
			_ => string.Empty,
		};
	}

	private static string ExtractFromAnthropicMessages(JsonObject body)
	{
		if (body["messages"] is not JsonArray messages)
		{
			return string.Empty;
		}

		var parts = new List<string>();
		var userCount = 0;

		for (var idx = messages.Count - 1; idx >= 0 && userCount < MaxUserMessages; idx--)
		{
			if (messages[idx] is not JsonObject msg)
			{
				continue;
			}

			if (msg["role"] is not JsonValue rv || !rv.TryGetValue<string>(out var role))
			{
				continue;
			}

			if (role == "user")
			{
				AppendUserMessageContent(msg["content"], parts);
				userCount++;
			}
		}

		return string.Join(" ", parts);
	}

	private static string ExtractFromOpenAiMessages(JsonObject body)
	{
		if (body["messages"] is not JsonArray messages)
		{
			return string.Empty;
		}

		var parts = new List<string>();
		var userCount = 0;

		for (var idx = messages.Count - 1; idx >= 0 && userCount < MaxUserMessages; idx--)
		{
			if (messages[idx] is not JsonObject msg)
			{
				continue;
			}

			if (msg["role"] is not JsonValue rv || !rv.TryGetValue<string>(out var role))
			{
				continue;
			}

			if (role == "user")
			{
				if (msg["content"] is JsonValue cv && cv.TryGetValue<string>(out var text))
				{
					parts.Add(text);
				}

				userCount++;
			}
			else if (role == "assistant" && msg["tool_calls"] is JsonArray toolCalls)
			{
				// Include function call arguments from the assistant's tool calls.
				foreach (var tc in toolCalls)
				{
					if (tc is JsonObject tcObj &&
						tcObj["function"] is JsonObject funcObj &&
						funcObj["arguments"] is JsonValue argVal &&
						argVal.TryGetValue<string>(out var args) &&
						!string.IsNullOrWhiteSpace(args))
					{
						parts.Add(args);
					}
				}
			}
		}

		return string.Join(" ", parts);
	}

	private static string ExtractFromOpenAiInput(JsonObject body)
	{
		if (body["input"] is not JsonArray input)
		{
			// String-form input — try to extract it directly as context.
			if (body["input"] is JsonValue sv && sv.TryGetValue<string>(out var inputText))
			{
				return inputText;
			}

			return string.Empty;
		}

		var parts = new List<string>();
		var userCount = 0;

		for (var idx = input.Count - 1; idx >= 0 && userCount < MaxUserMessages; idx--)
		{
			if (input[idx] is not JsonObject item)
			{
				continue;
			}

			if (item["role"] is JsonValue rv && rv.TryGetValue<string>(out var role) && role == "user")
			{
				if (item["content"] is JsonValue cv && cv.TryGetValue<string>(out var text))
				{
					parts.Add(text);
				}

				userCount++;
			}
		}

		return string.Join(" ", parts);
	}

	private static void AppendUserMessageContent(JsonNode? content, List<string> parts)
	{
		if (content is JsonValue sv && sv.TryGetValue<string>(out var text))
		{
			parts.Add(text);
			return;
		}

		// Array-form content (Anthropic style).
		if (content is JsonArray blocks)
		{
			foreach (var block in blocks)
			{
				if (block is JsonObject blockObj &&
					blockObj["type"] is JsonValue typeVal &&
					typeVal.TryGetValue<string>(out var blockType) &&
					blockType == "text" &&
					blockObj["text"] is JsonValue textVal &&
					textVal.TryGetValue<string>(out var blockText) &&
					!string.IsNullOrWhiteSpace(blockText))
				{
					parts.Add(blockText);
				}
			}
		}
	}
}
