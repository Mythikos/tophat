using System.Text.Json.Nodes;

namespace TopHat.Compression.CCR;

/// <summary>
/// Adds the synthetic <c>tophat_retrieve</c> tool definition to an OpenAI
/// <c>/v1/chat/completions</c> request body so the model is aware of CCR. The Chat Completions
/// tool shape nests the function descriptor under <c>function</c>, so the present-check looks at
/// <c>function.name</c> rather than the top-level <c>name</c>.
/// </summary>
internal static class OpenAIChatCompletionsCCRToolInjector
{
	/// <summary>
	/// Ensures an OpenAI Chat Completions request body exposes the <c>tophat_retrieve</c> tool to
	/// the model. Creates the top-level <c>tools</c> array if it does not already exist, then
	/// appends the tool definition unless a function tool with the same name is already present
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

		tools.Add(CCRToolDefinition.BuildOpenAIChatCompletionsToolDefinition());
		return true;
	}

	private static bool IsCCRTool(JsonObject toolObj)
	{
		if (toolObj["function"] is not JsonObject functionObj)
		{
			return false;
		}

		if (!functionObj.TryGetPropertyValue("name", out var nameNode) || nameNode is null)
		{
			return false;
		}

		return string.Equals(nameNode.GetValue<string>(), CCRToolDefinition.ToolName, StringComparison.Ordinal);
	}
}
