using System.Text.Json.Nodes;

namespace TopHat.Compression.CCR;

/// <summary>
/// Adds the synthetic <c>tophat_retrieve</c> tool definition to an OpenAI <c>/v1/responses</c>
/// request body so the model is aware of CCR. Responses uses a flat function-tool shape — name,
/// description, and parameters live at the top level — so the present-check looks at the
/// top-level <c>name</c> field directly.
/// </summary>
internal static class OpenAIResponsesCCRToolInjector
{
	/// <summary>
	/// Ensures an OpenAI Responses request body exposes the <c>tophat_retrieve</c> tool to the
	/// model. Creates the top-level <c>tools</c> array if it does not already exist, then appends
	/// the tool definition unless a function tool with the same name is already present
	/// (caller-defined or a leftover from a previous injection). Idempotent.
	/// </summary>
	/// <returns>
	/// <c>true</c> if the body was modified, <c>false</c> if no change was needed (tool already
	/// present).
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
				return false;
			}
		}

		tools.Add(CCRToolDefinition.BuildOpenAIResponsesToolDefinition());
		return true;
	}

	private static bool IsCCRTool(JsonObject toolObj)
	{
		if (!toolObj.TryGetPropertyValue("type", out var typeNode) || typeNode is null)
		{
			return false;
		}

		if (!string.Equals(typeNode.GetValue<string>(), "function", StringComparison.Ordinal))
		{
			return false;
		}

		if (!toolObj.TryGetPropertyValue("name", out var nameNode) || nameNode is null)
		{
			return false;
		}

		return string.Equals(nameNode.GetValue<string>(), CCRToolDefinition.ToolName, StringComparison.Ordinal);
	}
}
