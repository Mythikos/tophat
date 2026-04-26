using System.Text.Json.Nodes;
using TopHat.Compression.CCR;
using Xunit;

namespace TopHat.Tests.Compression.CCR;

public sealed class OpenAIChatCompletionsCCRToolInjectorTests
{
	[Fact]
	public void EnsureToolPresent_CreatesToolsArray_WhenMissing()
	{
		var body = new JsonObject();

		var modified = OpenAIChatCompletionsCCRToolInjector.EnsureToolPresent(body);

		Assert.True(modified);
		var tools = Assert.IsType<JsonArray>(body["tools"]);
		Assert.Single(tools);
		Assert.Equal("function", tools[0]!["type"]!.GetValue<string>());
		Assert.Equal(CCRToolDefinition.ToolName, tools[0]!["function"]!["name"]!.GetValue<string>());
	}

	[Fact]
	public void EnsureToolPresent_AppendsToExistingToolsArray()
	{
		var body = new JsonObject
		{
			["tools"] = new JsonArray(new JsonObject
			{
				["type"] = "function",
				["function"] = new JsonObject { ["name"] = "user_tool" },
			}),
		};

		var modified = OpenAIChatCompletionsCCRToolInjector.EnsureToolPresent(body);

		Assert.True(modified);
		var tools = (JsonArray)body["tools"]!;
		Assert.Equal(2, tools.Count);
		Assert.Equal("user_tool", tools[0]!["function"]!["name"]!.GetValue<string>());
		Assert.Equal(CCRToolDefinition.ToolName, tools[1]!["function"]!["name"]!.GetValue<string>());
	}

	[Fact]
	public void EnsureToolPresent_Idempotent_WhenAlreadyPresent()
	{
		var body = new JsonObject
		{
			["tools"] = new JsonArray(CCRToolDefinition.BuildOpenAIChatCompletionsToolDefinition()),
		};

		var modified = OpenAIChatCompletionsCCRToolInjector.EnsureToolPresent(body);

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
				["function"] = new JsonObject
				{
					["name"] = CCRToolDefinition.ToolName,
					["description"] = "user-authored version",
				},
			}),
		};

		var modified = OpenAIChatCompletionsCCRToolInjector.EnsureToolPresent(body);

		Assert.False(modified);
		var tools = (JsonArray)body["tools"]!;
		Assert.Single(tools);
		Assert.Equal("user-authored version", tools[0]!["function"]!["description"]!.GetValue<string>());
	}

	[Fact]
	public void BuildOpenAIChatCompletionsToolDefinition_ProducesNestedShape()
	{
		var tool = CCRToolDefinition.BuildOpenAIChatCompletionsToolDefinition();

		Assert.Equal("function", tool["type"]!.GetValue<string>());
		var function = (JsonObject)tool["function"]!;
		Assert.Equal(CCRToolDefinition.ToolName, function["name"]!.GetValue<string>());
		Assert.NotNull(function["description"]);
		var schema = (JsonObject)function["parameters"]!;
		Assert.Equal("object", schema["type"]!.GetValue<string>());
		var required = (JsonArray)schema["required"]!;
		Assert.Contains(required, node => node!.GetValue<string>() == CCRToolDefinition.RetrievalKeyField);
		Assert.NotNull(schema["properties"]![CCRToolDefinition.RetrievalKeyField]);
		Assert.NotNull(schema["properties"]!["ids"]);
		Assert.NotNull(schema["properties"]!["limit"]);
	}
}
