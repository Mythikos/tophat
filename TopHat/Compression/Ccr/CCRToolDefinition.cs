using System.Text.Json.Nodes;

namespace TopHat.Compression.CCR;

/// <summary>
/// Canonical definition of the synthetic <c>tophat_retrieve</c> tool that CCR injects into
/// compressed requests. Shared constants live here so the compressor (which injects the tool
/// definition) and the orchestrator (which recognises incoming tool_use blocks) agree on names.
/// </summary>
public static class CCRToolDefinition
{
	/// <summary>
	/// Name of the synthetic tool exposed to the model when CCR is active. Chosen to be
	/// recognisable, unlikely to collide with user-defined tools, and short enough not to inflate
	/// token count. If a caller has already defined a tool with this name, injection is skipped
	/// (detected by <see cref="AnthropicCCRToolInjector"/>) and CCR is silently disabled for
	/// that request to avoid overwriting.
	/// </summary>
	public const string ToolName = "tophat_retrieve";

	/// <summary>
	/// Name of the retrieval-key field embedded in <c>_tophat_compression</c> metadata. The model
	/// sees this key in the compressed payload and echoes it back when calling <see cref="ToolName"/>.
	/// </summary>
	public const string RetrievalKeyField = "retrieval_key";

	private const string ToolDescription =
		"Retrieve items that TopHat's compressor elided from a tool_result. " +
		"Call this tool ONLY when the compressed summary in _tophat_compression is insufficient to answer the user's question. " +
		"Pass the `retrieval_key` from the _tophat_compression metadata. " +
		"Use `ids` to request specific items when the elided entries have integer `id` fields. " +
		"If retrieval returns an empty array, the items have expired or the key is invalid — answer from the context you have.";

	/// <summary>
	/// Builds an Anthropic-formatted tool definition object. The caller is responsible for
	/// appending it to the request's <c>tools</c> array via <see cref="AnthropicCCRToolInjector"/>
	/// — this method just produces the shape.
	/// </summary>
	public static JsonObject BuildAnthropicToolDefinition()
	{
		return new JsonObject
		{
			["name"] = ToolName,
			["description"] = ToolDescription,
			["input_schema"] = BuildParameterSchema(),
		};
	}

	/// <summary>
	/// Builds an OpenAI Chat Completions tool definition. The function descriptor is nested under
	/// <c>function</c> with <c>type: "function"</c> at the top level, matching the
	/// <c>/v1/chat/completions</c> tools array shape.
	/// </summary>
	public static JsonObject BuildOpenAIChatCompletionsToolDefinition()
	{
		return new JsonObject
		{
			["type"] = "function",
			["function"] = new JsonObject
			{
				["name"] = ToolName,
				["description"] = ToolDescription,
				["parameters"] = BuildParameterSchema(),
			},
		};
	}

	/// <summary>
	/// Builds an OpenAI Responses tool definition. The Responses API uses a flat shape — name,
	/// description, and parameters live at the top level alongside <c>type: "function"</c> rather
	/// than nested under <c>function</c>.
	/// </summary>
	public static JsonObject BuildOpenAIResponsesToolDefinition()
	{
		return new JsonObject
		{
			["type"] = "function",
			["name"] = ToolName,
			["description"] = ToolDescription,
			["parameters"] = BuildParameterSchema(),
		};
	}

	private static JsonObject BuildParameterSchema()
	{
		return new JsonObject
		{
			["type"] = "object",
			["required"] = new JsonArray("retrieval_key"),
			["properties"] = new JsonObject
			{
				["retrieval_key"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "The key surfaced by the compressor in the _tophat_compression.retrieval_key metadata field.",
				},
				["ids"] = new JsonObject
				{
					["type"] = "array",
					["items"] = new JsonObject { ["type"] = "integer" },
					["description"] = "Specific integer IDs to retrieve. When omitted, returns the first `limit` elided items.",
				},
				["limit"] = new JsonObject
				{
					["type"] = "integer",
					["description"] = "Maximum items to return. Defaults to 10; the server enforces an upper ceiling.",
					["default"] = 10,
				},
			},
		};
	}
}
