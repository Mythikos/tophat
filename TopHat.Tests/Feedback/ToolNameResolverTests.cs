using System.Text.Json.Nodes;
using TopHat.Feedback;
using TopHat.Providers;
using Xunit;

namespace TopHat.Tests.Feedback;

/// <summary>
/// Unit tests for <see cref="ToolNameResolver"/> across all three target shapes. Locked-in
/// because the recording layer's tool_name keying depends on these mappings working
/// correctly for every provider's wire format.
/// </summary>
public sealed class ToolNameResolverTests
{
	[Fact]
	public void Anthropic_ResolvesByToolUseId()
	{
		var body = JsonNode.Parse("""
		{
			"messages": [
				{ "role": "user", "content": "x" },
				{ "role": "assistant", "content": [
					{ "type": "tool_use", "id": "tu_1", "name": "get_logs", "input": {} },
					{ "type": "tool_use", "id": "tu_2", "name": "get_users", "input": {} }
				]},
				{ "role": "user", "content": [
					{ "type": "tool_result", "tool_use_id": "tu_1", "content": "..." }
				]}
			]
		}
		""")!.AsObject();

		Assert.Equal("get_logs", ToolNameResolver.Resolve(body, TopHatTarget.AnthropicMessages, "tu_1"));
		Assert.Equal("get_users", ToolNameResolver.Resolve(body, TopHatTarget.AnthropicMessages, "tu_2"));
		Assert.Null(ToolNameResolver.Resolve(body, TopHatTarget.AnthropicMessages, "tu_unknown"));
	}

	[Fact]
	public void OpenAIChat_ResolvesByToolCallId()
	{
		var body = JsonNode.Parse("""
		{
			"messages": [
				{ "role": "user", "content": "x" },
				{ "role": "assistant", "tool_calls": [
					{ "id": "call_1", "type": "function", "function": { "name": "get_logs", "arguments": "{}" } },
					{ "id": "call_2", "type": "function", "function": { "name": "search_users", "arguments": "{}" } }
				]}
			]
		}
		""")!.AsObject();

		Assert.Equal("get_logs", ToolNameResolver.Resolve(body, TopHatTarget.OpenAIChatCompletions, "call_1"));
		Assert.Equal("search_users", ToolNameResolver.Resolve(body, TopHatTarget.OpenAIChatCompletions, "call_2"));
	}

	[Fact]
	public void OpenAIResponses_ResolvesByCallId()
	{
		var body = JsonNode.Parse("""
		{
			"input": [
				{ "role": "user", "content": "x" },
				{ "type": "function_call", "call_id": "fc_1", "name": "get_logs", "arguments": "{}" },
				{ "type": "function_call", "call_id": "fc_2", "name": "fetch_orders", "arguments": "{}" }
			]
		}
		""")!.AsObject();

		Assert.Equal("get_logs", ToolNameResolver.Resolve(body, TopHatTarget.OpenAIResponses, "fc_1"));
		Assert.Equal("fetch_orders", ToolNameResolver.Resolve(body, TopHatTarget.OpenAIResponses, "fc_2"));
	}

	[Fact]
	public void ResolveByRetrievalKey_FindsToolThroughCompressionMetadata()
	{
		// Anthropic shape: tool_result content embeds the retrieval_key. Resolver walks back
		// to the matching tool_use to get the name.
		var compressedContent = """[{"id":1,"data":"x"},{"_tophat_compression":{"retrieval_key":"abc123"}}]""";
		var body = JsonNode.Parse($$"""
		{
			"messages": [
				{ "role": "user", "content": "q" },
				{ "role": "assistant", "content": [
					{ "type": "tool_use", "id": "tu_1", "name": "fetch_records", "input": {} }
				]},
				{ "role": "user", "content": [
					{ "type": "tool_result", "tool_use_id": "tu_1", "content": {{System.Text.Json.JsonSerializer.Serialize(compressedContent)}} }
				]}
			]
		}
		""")!.AsObject();

		Assert.Equal("fetch_records", ToolNameResolver.ResolveByRetrievalKey(body, TopHatTarget.AnthropicMessages, "abc123"));
		Assert.Null(ToolNameResolver.ResolveByRetrievalKey(body, TopHatTarget.AnthropicMessages, "wrong_key"));
	}

	[Fact]
	public void ResolveByRetrievalKey_OpenAIChat()
	{
		var compressedContent = """[{"_tophat_compression":{"retrieval_key":"key123"}}]""";
		var body = JsonNode.Parse($$"""
		{
			"messages": [
				{ "role": "user", "content": "q" },
				{ "role": "assistant", "tool_calls": [
					{ "id": "call_x", "type": "function", "function": { "name": "list_files", "arguments": "{}" } }
				]},
				{ "role": "tool", "tool_call_id": "call_x", "content": {{System.Text.Json.JsonSerializer.Serialize(compressedContent)}} }
			]
		}
		""")!.AsObject();

		Assert.Equal("list_files", ToolNameResolver.ResolveByRetrievalKey(body, TopHatTarget.OpenAIChatCompletions, "key123"));
	}

	[Fact]
	public void EmptyOrNullId_ReturnsNull()
	{
		var body = new JsonObject();
		Assert.Null(ToolNameResolver.Resolve(body, TopHatTarget.AnthropicMessages, ""));
		Assert.Null(ToolNameResolver.ResolveByRetrievalKey(body, TopHatTarget.AnthropicMessages, ""));
	}

	[Fact]
	public void ExtractToolUseId_PicksRightFieldPerTarget()
	{
		var anthBlock = JsonNode.Parse("""{ "type": "tool_result", "tool_use_id": "tu_X", "content": "" }""")!.AsObject();
		Assert.Equal("tu_X", ToolNameResolver.ExtractToolUseId(anthBlock, TopHatTarget.AnthropicMessages));

		var chatBlock = JsonNode.Parse("""{ "role": "tool", "tool_call_id": "call_X", "content": "" }""")!.AsObject();
		Assert.Equal("call_X", ToolNameResolver.ExtractToolUseId(chatBlock, TopHatTarget.OpenAIChatCompletions));

		var respBlock = JsonNode.Parse("""{ "type": "function_call_output", "call_id": "fc_X", "output": "" }""")!.AsObject();
		Assert.Equal("fc_X", ToolNameResolver.ExtractToolUseId(respBlock, TopHatTarget.OpenAIResponses));
	}
}
