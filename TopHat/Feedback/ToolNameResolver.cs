using System.Text.Json.Nodes;
using TopHat.Providers;

namespace TopHat.Feedback;

/// <summary>
/// Resolves the upstream tool name for a tool-result block by walking the request body to
/// find the matching tool_use / tool_call / function_call. Used by the compressor and CCR
/// orchestrators so feedback events can be keyed by tool name without requiring the caller
/// to plumb the name through every layer.
/// </summary>
/// <remarks>
/// <para>
/// Returns <c>null</c> when the name can't be resolved — malformed body, ID mismatch, or
/// out-of-scope target. Callers should treat null as "skip recording for this event"
/// rather than as an error, since feedback is best-effort.
/// </para>
/// </remarks>
internal static class ToolNameResolver
{
	/// <summary>
	/// Resolves a tool name by ID against the appropriate target's body shape:
	/// <list type="bullet">
	///   <item><description><see cref="TopHatTarget.AnthropicMessages"/>: searches for
	///   <c>messages[N].content[M].tool_use</c> with matching <c>id</c>.</description></item>
	///   <item><description><see cref="TopHatTarget.OpenAIChatCompletions"/>: searches for
	///   <c>messages[N].tool_calls[M]</c> with matching <c>id</c>.</description></item>
	///   <item><description><see cref="TopHatTarget.OpenAIResponses"/>: searches for
	///   <c>input[N]</c> items with <c>type="function_call"</c> and matching
	///   <c>call_id</c>.</description></item>
	/// </list>
	/// </summary>
	public static string? Resolve(JsonObject body, TopHatTarget target, string id)
	{
		if (string.IsNullOrEmpty(id))
		{
			return null;
		}

		return target switch
		{
			TopHatTarget.AnthropicMessages => ResolveAnthropic(body, id),
			TopHatTarget.OpenAIChatCompletions => ResolveOpenAIChat(body, id),
			TopHatTarget.OpenAIResponses => ResolveOpenAIResponses(body, id),
			_ => null,
		};
	}

	/// <summary>
	/// Extracts the tool ID from a tool-result block. Same per-target dispatch as
	/// <see cref="Resolve"/> — this helper is what the compressor uses to harvest the ID
	/// from a <c>ToolResultRef.Owner</c> JsonObject.
	/// </summary>
	public static string? ExtractToolUseId(JsonObject toolResultBlock, TopHatTarget target) => target switch
	{
		TopHatTarget.AnthropicMessages => toolResultBlock["tool_use_id"]?.GetValue<string>(),
		TopHatTarget.OpenAIChatCompletions => toolResultBlock["tool_call_id"]?.GetValue<string>(),
		TopHatTarget.OpenAIResponses => toolResultBlock["call_id"]?.GetValue<string>(),
		_ => null,
	};

	/// <summary>
	/// Resolves the tool name associated with a CCR retrieval_key by scanning the request body
	/// for a tool-result whose embedded <c>_tophat_compression.retrieval_key</c> matches, then
	/// finding the tool_use that produced it. Used by CCR orchestrators when fulfilling
	/// <c>tophat_retrieve</c> calls so retrieval events can be keyed by tool name.
	/// </summary>
	public static string? ResolveByRetrievalKey(JsonObject body, TopHatTarget target, string retrievalKey)
	{
		if (string.IsNullOrEmpty(retrievalKey))
		{
			return null;
		}

		// Walk the body's tool-result string slots, parse each as JSON, look for the
		// compressor's metadata block carrying a matching retrieval_key. When found, harvest
		// the owning tool-result block's tool_use_id and resolve via Resolve().
		var ownerAndId = FindToolResultByRetrievalKey(body, target, retrievalKey);
		if (ownerAndId is null)
		{
			return null;
		}

		var (_, toolUseId) = ownerAndId.Value;
		return Resolve(body, target, toolUseId);
	}

	private static (JsonObject Owner, string ToolUseId)? FindToolResultByRetrievalKey(JsonObject body, TopHatTarget target, string retrievalKey)
	{
		// Per-target shape: each yields (toolResultBlock, contentString) candidates.
		// We parse the contentString as a JSON array, scan for an item with
		// _tophat_compression.retrieval_key matching, return when found.
		foreach (var (block, content) in EnumerateToolResultContents(body, target))
		{
			if (!ContentContainsRetrievalKey(content, retrievalKey))
			{
				continue;
			}

			var toolUseId = ExtractToolUseId(block, target);
			if (!string.IsNullOrEmpty(toolUseId))
			{
				return (block, toolUseId);
			}
		}

		return null;
	}

	private static IEnumerable<(JsonObject Block, string Content)> EnumerateToolResultContents(JsonObject body, TopHatTarget target)
	{
		switch (target)
		{
			case TopHatTarget.AnthropicMessages:
				if (body["messages"] is JsonArray messages)
				{
					foreach (var msg in messages)
					{
						if (msg is not JsonObject msgObj || msgObj["content"] is not JsonArray contentArr)
						{
							continue;
						}

						foreach (var blk in contentArr)
						{
							if (blk is JsonObject blkObj
								&& blkObj["type"]?.GetValue<string>() == "tool_result"
								&& blkObj["content"]?.GetValue<string>() is string anthropicContent)
							{
								yield return (blkObj, anthropicContent);
							}
						}
					}
				}
				break;

			case TopHatTarget.OpenAIChatCompletions:
				if (body["messages"] is JsonArray openaiMessages)
				{
					foreach (var msg in openaiMessages)
					{
						if (msg is JsonObject msgObj
							&& msgObj["role"]?.GetValue<string>() == "tool"
							&& msgObj["content"]?.GetValue<string>() is string chatContent)
						{
							yield return (msgObj, chatContent);
						}
					}
				}
				break;

			case TopHatTarget.OpenAIResponses:
				if (body["input"] is JsonArray input)
				{
					foreach (var item in input)
					{
						if (item is JsonObject itemObj
							&& itemObj["type"]?.GetValue<string>() == "function_call_output"
							&& itemObj["output"]?.GetValue<string>() is string respContent)
						{
							yield return (itemObj, respContent);
						}
					}
				}
				break;
		}
	}

	private static bool ContentContainsRetrievalKey(string content, string retrievalKey)
	{
		if (!content.Contains(retrievalKey, StringComparison.Ordinal))
		{
			// Fast pre-check — full JSON parse is more expensive and most tool_results won't
			// have any retrieval_key at all.
			return false;
		}

		try
		{
			var parsed = JsonNode.Parse(content);
			if (parsed is not JsonArray arr)
			{
				return false;
			}

			foreach (var item in arr)
			{
				if (item is JsonObject obj
					&& obj["_tophat_compression"] is JsonObject metadata
					&& metadata["retrieval_key"]?.GetValue<string>() == retrievalKey)
				{
					return true;
				}
			}
		}
		catch (System.Text.Json.JsonException)
		{
		}

		return false;
	}

	private static string? ResolveAnthropic(JsonObject body, string toolUseId)
	{
		if (body["messages"] is not JsonArray messages)
		{
			return null;
		}

		foreach (var msg in messages)
		{
			if (msg is not JsonObject msgObj || msgObj["content"] is not JsonArray content)
			{
				continue;
			}

			foreach (var block in content)
			{
				if (block is not JsonObject blockObj)
				{
					continue;
				}

				if (blockObj["type"]?.GetValue<string>() != "tool_use")
				{
					continue;
				}

				if (blockObj["id"]?.GetValue<string>() == toolUseId)
				{
					return blockObj["name"]?.GetValue<string>();
				}
			}
		}

		return null;
	}

	private static string? ResolveOpenAIChat(JsonObject body, string toolCallId)
	{
		if (body["messages"] is not JsonArray messages)
		{
			return null;
		}

		foreach (var msg in messages)
		{
			if (msg is not JsonObject msgObj || msgObj["tool_calls"] is not JsonArray toolCalls)
			{
				continue;
			}

			foreach (var call in toolCalls)
			{
				if (call is not JsonObject callObj)
				{
					continue;
				}

				if (callObj["id"]?.GetValue<string>() == toolCallId)
				{
					// Chat Completions nests the function descriptor under "function".
					return (callObj["function"] as JsonObject)?["name"]?.GetValue<string>();
				}
			}
		}

		return null;
	}

	private static string? ResolveOpenAIResponses(JsonObject body, string callId)
	{
		if (body["input"] is not JsonArray input)
		{
			return null;
		}

		foreach (var item in input)
		{
			if (item is not JsonObject itemObj)
			{
				continue;
			}

			if (itemObj["type"]?.GetValue<string>() != "function_call")
			{
				continue;
			}

			if (itemObj["call_id"]?.GetValue<string>() == callId)
			{
				return itemObj["name"]?.GetValue<string>();
			}
		}

		return null;
	}
}
