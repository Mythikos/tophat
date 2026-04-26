using System.Text.Json.Nodes;
using TopHat.Compression.CCR;
using Xunit;

namespace TopHat.Tests.Compression.CCR;

public sealed class AnthropicCCRToolInjectorTests
{
	[Fact]
	public void EnsureToolPresent_CreatesToolsArray_WhenMissing()
	{
		var body = new JsonObject();

		var modified = AnthropicCCRToolInjector.EnsureToolPresent(body);

		Assert.True(modified);
		var tools = Assert.IsType<JsonArray>(body["tools"]);
		Assert.Single(tools);
		Assert.Equal(CCRToolDefinition.ToolName, tools[0]!["name"]!.GetValue<string>());
	}

	[Fact]
	public void EnsureToolPresent_AppendsToExistingToolsArray()
	{
		var body = new JsonObject
		{
			["tools"] = new JsonArray(new JsonObject { ["name"] = "user_tool" }),
		};

		var modified = AnthropicCCRToolInjector.EnsureToolPresent(body);

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
			["tools"] = new JsonArray(CCRToolDefinition.BuildAnthropicToolDefinition()),
		};

		var modified = AnthropicCCRToolInjector.EnsureToolPresent(body);

		Assert.False(modified);
		var tools = (JsonArray)body["tools"]!;
		Assert.Single(tools);
	}

	[Fact]
	public void EnsureToolPresent_DoesNotOverrideConflictingUserTool()
	{
		// Belt-and-suspenders: if a caller already defined a tool named tophat_retrieve, we
		// treat it as "present" and skip injection. (CCR will then not work for that request —
		// the orchestrator won't intercept user-authored tools; documented limitation.)
		var body = new JsonObject
		{
			["tools"] = new JsonArray(new JsonObject
			{
				["name"] = CCRToolDefinition.ToolName,
				["description"] = "user-authored version",
			}),
		};

		var modified = AnthropicCCRToolInjector.EnsureToolPresent(body);

		Assert.False(modified);
		var tools = (JsonArray)body["tools"]!;
		Assert.Single(tools);
		Assert.Equal("user-authored version", tools[0]!["description"]!.GetValue<string>());
	}

	[Fact]
	public void BuildAnthropicToolDefinition_ProducesExpectedSchema()
	{
		var tool = CCRToolDefinition.BuildAnthropicToolDefinition();

		Assert.Equal(CCRToolDefinition.ToolName, tool["name"]!.GetValue<string>());
		Assert.NotNull(tool["description"]);
		var schema = (JsonObject)tool["input_schema"]!;
		Assert.Equal("object", schema["type"]!.GetValue<string>());
		var required = (JsonArray)schema["required"]!;
		Assert.Contains(required, node => node!.GetValue<string>() == CCRToolDefinition.RetrievalKeyField);
		Assert.NotNull(schema["properties"]![CCRToolDefinition.RetrievalKeyField]);
		Assert.NotNull(schema["properties"]!["ids"]);
		Assert.NotNull(schema["properties"]!["limit"]);
	}
}
