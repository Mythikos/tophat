using System.Text.Json.Nodes;

namespace TopHat.Compression.CCR;

/// <summary>
/// Adds the synthetic <c>tophat_retrieve</c> tool definition to an Anthropic
/// <c>/v1/messages</c> request body so the model is aware of CCR. Used by
/// <c>JsonContextCompressorTransform</c> once it knows at least one tool_result's dropped items
/// have been registered in the <see cref="ICompressionContextStore"/>.
/// </summary>
internal static class AnthropicCCRToolInjector
{
	/// <summary>
	/// Ensures an Anthropic request body exposes the <c>tophat_retrieve</c> tool to the model.
	/// Creates the top-level <c>tools</c> array if it does not already exist, then appends the
	/// tool definition unless a tool with the same name is already present (caller-defined or a
	/// leftover from a previous injection). Idempotent.
	/// </summary>
	/// <returns>
	/// <c>true</c> if the body was modified (tools array created and/or CCR tool appended),
	/// <c>false</c> if no change was needed (tool already present).
	/// </returns>
	public static bool EnsureToolPresent(JsonObject body)
	{
		ArgumentNullException.ThrowIfNull(body);

		if (body["tools"] is not JsonArray tools)
		{
			tools = new JsonArray();
			body["tools"] = tools;
		}

		foreach (var existing in tools)
		{
			if (existing is JsonObject toolObj && IsCCRTool(toolObj))
			{
				// Already present — idempotent path.
				return false;
			}
		}

		tools.Add(CCRToolDefinition.BuildAnthropicToolDefinition());
		return true;
	}

	private static bool IsCCRTool(JsonObject toolObj)
	{
		if (!toolObj.TryGetPropertyValue("name", out var nameNode) || nameNode is null)
		{
			return false;
		}

		return string.Equals(nameNode.GetValue<string>(), CCRToolDefinition.ToolName, StringComparison.Ordinal);
	}
}
