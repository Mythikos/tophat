using System.Text.Json.Nodes;
using TopHat.Providers;
using TopHat.Transforms.JsonContext.Messages;
using Xunit;

namespace TopHat.Tests.Transforms.JsonContext;

public sealed class ToolResultLocatorTests
{
	#region Anthropic /v1/messages

	[Fact]
	public void Find_AnthropicStringContentToolResult_Located()
	{
		var body = JsonNode.Parse("""
			{
				"messages": [
					{ "role": "user", "content": "hello" },
					{
						"role": "user",
						"content": [
							{ "type": "tool_result", "tool_use_id": "tu_1", "content": "[{\"key\":\"value\"}]" }
						]
					}
				]
			}
			""")!.AsObject();

		var refs = ToolResultLocator.Find(body, TopHatTarget.AnthropicMessages);

		Assert.Single(refs);
		Assert.Equal("content", refs[0].Key);
		Assert.Equal("[{\"key\":\"value\"}]", refs[0].Text);
	}

	[Fact]
	public void Find_AnthropicArrayContentToolResult_LocatesEachTextBlock()
	{
		var body = JsonNode.Parse("""
			{
				"messages": [
					{
						"role": "user",
						"content": [
							{
								"type": "tool_result",
								"tool_use_id": "tu_1",
								"content": [
									{ "type": "text", "text": "[{\"first\":1}]" },
									{ "type": "text", "text": "[{\"second\":2}]" }
								]
							}
						]
					}
				]
			}
			""")!.AsObject();

		var refs = ToolResultLocator.Find(body, TopHatTarget.AnthropicMessages);

		Assert.Equal(2, refs.Count);
		Assert.Equal("text", refs[0].Key);
		Assert.Equal("[{\"first\":1}]", refs[0].Text);
		Assert.Equal("[{\"second\":2}]", refs[1].Text);
	}

	[Fact]
	public void Find_Anthropic_FrozenMessagesSkipped()
	{
		var body = JsonNode.Parse("""
			{
				"messages": [
					{
						"role": "user",
						"content": [
							{ "type": "tool_result", "tool_use_id": "tu_old", "content": "[{\"old\":true}]" }
						]
					},
					{
						"role": "user",
						"content": [
							{ "type": "tool_result", "tool_use_id": "tu_new", "content": "[{\"new\":true}]" }
						]
					}
				]
			}
			""")!.AsObject();

		var refs = ToolResultLocator.Find(body, TopHatTarget.AnthropicMessages, frozenMessageCount: 1);

		Assert.Single(refs);
		Assert.Equal("[{\"new\":true}]", refs[0].Text);
	}

	#endregion

	#region OpenAI /v1/chat/completions

	[Fact]
	public void Find_OpenAiChatToolRole_Located()
	{
		var body = JsonNode.Parse("""
			{
				"messages": [
					{ "role": "user", "content": "query" },
					{ "role": "tool", "tool_call_id": "call_1", "content": "[{\"result\":42}]" }
				]
			}
			""")!.AsObject();

		var refs = ToolResultLocator.Find(body, TopHatTarget.OpenAIChatCompletions);

		Assert.Single(refs);
		Assert.Equal("content", refs[0].Key);
		Assert.Equal("[{\"result\":42}]", refs[0].Text);
	}

	[Fact]
	public void Find_OpenAiChat_NonToolRolesIgnored()
	{
		var body = JsonNode.Parse("""
			{
				"messages": [
					{ "role": "system", "content": "[{\"should\":\"not\",\"match\":true}]" },
					{ "role": "user", "content": "ask" },
					{ "role": "assistant", "content": "answer" }
				]
			}
			""")!.AsObject();

		var refs = ToolResultLocator.Find(body, TopHatTarget.OpenAIChatCompletions);

		Assert.Empty(refs);
	}

	#endregion

	#region OpenAI /v1/responses

	[Fact]
	public void Find_OpenAiResponsesFunctionCallOutput_Located()
	{
		var body = JsonNode.Parse("""
			{
				"input": [
					{ "role": "user", "content": "query" },
					{ "type": "function_call_output", "call_id": "call_1", "output": "[{\"rows\":[1,2,3]}]" }
				]
			}
			""")!.AsObject();

		var refs = ToolResultLocator.Find(body, TopHatTarget.OpenAIResponses);

		Assert.Single(refs);
		Assert.Equal("output", refs[0].Key);
		Assert.Equal("[{\"rows\":[1,2,3]}]", refs[0].Text);
	}

	[Fact]
	public void Find_OpenAiResponses_StringInput_ReturnsEmpty()
	{
		// When input is a plain string there are no tool results.
		var body = JsonNode.Parse("""
			{ "input": "what is the capital of France?" }
			""")!.AsObject();

		var refs = ToolResultLocator.Find(body, TopHatTarget.OpenAIResponses);

		Assert.Empty(refs);
	}

	#endregion

	#region Malformed / unknown targets

	[Fact]
	public void Find_UnknownTarget_ReturnsEmpty()
	{
		var body = new JsonObject();
		Assert.Empty(ToolResultLocator.Find(body, TopHatTarget.Unknown));
	}

	[Fact]
	public void Find_MissingMessagesArray_ReturnsEmpty()
	{
		var body = JsonNode.Parse("{}")!.AsObject();
		Assert.Empty(ToolResultLocator.Find(body, TopHatTarget.AnthropicMessages));
		Assert.Empty(ToolResultLocator.Find(body, TopHatTarget.OpenAIChatCompletions));
	}

	#endregion
}
