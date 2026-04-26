using System.Text.Json.Nodes;
using TopHat.Compression.CCR;
using Xunit;

namespace TopHat.Tests.Compression.CCR;

public sealed class OpenAIResponsesCCRToolInjectorTests
{
	[Fact]
	public void EnsureToolPresent_CreatesToolsArray_WhenMissing()
	{
		var body = new JsonObject();

		var modified = OpenAIResponsesCCRToolInjector.EnsureToolPresent(body);

		Assert.True(modified);
		var tools = Assert.IsType<JsonArray>(body["tools"]);
		Assert.Single(tools);
		Assert.Equal("function", tools[0]!["type"]!.GetValue<string>());
		Assert.Equal(CCRToolDefinition.ToolName, tools[0]!["name"]!.GetValue<string>());
	}

	[Fact]
	public void EnsureToolPresent_AppendsToExistingToolsArray()
	{
		var body = new JsonObject
		{
			["tools"] = new JsonArray(new JsonObject
			{
				["type"] = "function",
				["name"] = "user_tool",
			}),
		};

		var modified = OpenAIResponsesCCRToolInjector.EnsureToolPresent(body);

		Assert.True(modified);
		var tools = (JsonArray)body["tools"]!;
		Assert.Equal(2, tools.Count);
		Assert.Equal("user_tool", tools[0]!["name"]!.GetValue<string>());
		Assert.Equal(CCRToolDefinition.ToolName, tools[1]!["name"]!.GetValue<string>());
	}

	[Fact]
	public void EnsureToolPresent_Idempotent_WhenAlreadyPresent()
	{
		var body = new JsonObject
		{
			["tools"] = new JsonArray(CCRToolDefinition.BuildOpenAIResponsesToolDefinition()),
		};

		var modified = OpenAIResponsesCCRToolInjector.EnsureToolPresent(body);

		Assert.False(modified);
		var tools = (JsonArray)body["tools"]!;
		Assert.Single(tools);
	}

	[Fact]
	public void EnsureToolPresent_DoesNotOverrideConflictingUserTool()
	{
		var body = new JsonObject
		{
			["tools"] = new JsonArray(new JsonObject
			{
				["type"] = "function",
				["name"] = CCRToolDefinition.ToolName,
				["description"] = "user-authored version",
			}),
		};

		var modified = OpenAIResponsesCCRToolInjector.EnsureToolPresent(body);

		Assert.False(modified);
		var tools = (JsonArray)body["tools"]!;
		Assert.Single(tools);
		Assert.Equal("user-authored version", tools[0]!["description"]!.GetValue<string>());
	}

	[Fact]
	public void EnsureToolPresent_IgnoresNonFunctionTools_WithMatchingName()
	{
		// A non-function tool (e.g. type=file_search) that happens to share the name should NOT
		// block CCR injection — the present-check is scoped to function-typed tools only.
		var body = new JsonObject
		{
			["tools"] = new JsonArray(new JsonObject
			{
				["type"] = "file_search",
				["name"] = CCRToolDefinition.ToolName,
			}),
		};

		var modified = OpenAIResponsesCCRToolInjector.EnsureToolPresent(body);

		Assert.True(modified);
		var tools = (JsonArray)body["tools"]!;
		Assert.Equal(2, tools.Count);
	}

	[Fact]
	public void BuildOpenAIResponsesToolDefinition_ProducesFlatShape()
	{
		var tool = CCRToolDefinition.BuildOpenAIResponsesToolDefinition();

		Assert.Equal("function", tool["type"]!.GetValue<string>());
		Assert.Equal(CCRToolDefinition.ToolName, tool["name"]!.GetValue<string>());
		Assert.NotNull(tool["description"]);
		// Flat shape: parameters at top level, no nested function field.
		Assert.Null(tool["function"]);
		var schema = (JsonObject)tool["parameters"]!;
		Assert.Equal("object", schema["type"]!.GetValue<string>());
		var required = (JsonArray)schema["required"]!;
		Assert.Contains(required, node => node!.GetValue<string>() == CCRToolDefinition.RetrievalKeyField);
		Assert.NotNull(schema["properties"]![CCRToolDefinition.RetrievalKeyField]);
		Assert.NotNull(schema["properties"]!["ids"]);
		Assert.NotNull(schema["properties"]!["limit"]);
	}
}
