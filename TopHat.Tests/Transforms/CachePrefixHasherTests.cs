using System.Text.Json.Nodes;
using TopHat.Providers;
using TopHat.Transforms;
using Xunit;

namespace TopHat.Tests.Transforms;

public sealed class CachePrefixHasherTests
{
	private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

	#region Anthropic — cache_control marker dispatch

	[Fact]
	public void Anthropic_ReturnsNull_WhenNoMarker()
	{
		var body = Parse("""{"messages":[{"role":"user","content":"hi"}]}""");

		Assert.Null(CachePrefixHasher.Hash(body, TopHatTarget.AnthropicMessages));
	}

	[Fact]
	public void Anthropic_ReturnsValue_WhenMarkerPresent()
	{
		var body = Parse("""{"messages":[{"role":"user","content":[{"type":"text","text":"x","cache_control":{"type":"ephemeral"}}]}]}""");

		var hash = CachePrefixHasher.Hash(body, TopHatTarget.AnthropicMessages);

		Assert.NotNull(hash);
		Assert.Equal(64, hash.Length); // SHA-256 hex
	}

	[Fact]
	public void Anthropic_StableForIdenticalPrefix()
	{
		// Same content before "cache_control"; downstream differs but cache prefix is stable.
		var bodyA = Parse("""{"messages":[{"role":"user","content":[{"type":"text","text":"x","cache_control":{"type":"ephemeral"}}]}],"extra":"a"}""");
		var bodyB = Parse("""{"messages":[{"role":"user","content":[{"type":"text","text":"x","cache_control":{"type":"ephemeral"}}]}],"extra":"b"}""");

		Assert.Equal(
			CachePrefixHasher.Hash(bodyA, TopHatTarget.AnthropicMessages),
			CachePrefixHasher.Hash(bodyB, TopHatTarget.AnthropicMessages));
	}

	[Fact]
	public void Anthropic_DiffersWhenPrefixChanges()
	{
		var original = Parse("""{"messages":[{"role":"user","content":[{"type":"text","text":"x","cache_control":{"type":"ephemeral"}}]}]}""");
		var mutated = Parse("""{"messages":[{"role":"user","content":[{"type":"text","text":"y","cache_control":{"type":"ephemeral"}}]}]}""");

		Assert.NotEqual(
			CachePrefixHasher.Hash(original, TopHatTarget.AnthropicMessages),
			CachePrefixHasher.Hash(mutated, TopHatTarget.AnthropicMessages));
	}

	#endregion

	#region OpenAI Chat Completions — last-message-excluded prefix

	[Fact]
	public void OpenAIChat_ReturnsNull_WhenSingleMessage()
	{
		// Single-message conversation: nothing to cache, no detectable prefix.
		var body = Parse("""{"messages":[{"role":"user","content":"hi"}]}""");

		Assert.Null(CachePrefixHasher.Hash(body, TopHatTarget.OpenAIChatCompletions));
	}

	[Fact]
	public void OpenAIChat_HashesAllButLastMessage()
	{
		// 3-message conversation. Hash should cover messages[0] and messages[1] but NOT messages[2].
		var bodyA = Parse("""
			{"messages":[
				{"role":"system","content":"you are helpful"},
				{"role":"user","content":"first turn"},
				{"role":"user","content":"latest turn from A"}
			]}
			""");
		var bodyB = Parse("""
			{"messages":[
				{"role":"system","content":"you are helpful"},
				{"role":"user","content":"first turn"},
				{"role":"user","content":"completely different latest turn from B"}
			]}
			""");

		// Same prefix (system + first turn) → same hash even though last messages differ.
		Assert.Equal(
			CachePrefixHasher.Hash(bodyA, TopHatTarget.OpenAIChatCompletions),
			CachePrefixHasher.Hash(bodyB, TopHatTarget.OpenAIChatCompletions));
	}

	[Fact]
	public void OpenAIChat_DetectsPrefixMutation()
	{
		// Mutating the system message (which IS in the prefix) shifts the hash.
		var original = Parse("""
			{"messages":[
				{"role":"system","content":"you are helpful"},
				{"role":"user","content":"latest"}
			]}
			""");
		var mutated = Parse("""
			{"messages":[
				{"role":"system","content":"you are very helpful"},
				{"role":"user","content":"latest"}
			]}
			""");

		Assert.NotEqual(
			CachePrefixHasher.Hash(original, TopHatTarget.OpenAIChatCompletions),
			CachePrefixHasher.Hash(mutated, TopHatTarget.OpenAIChatCompletions));
	}

	#endregion

	#region OpenAI Responses — input array, last-item-excluded

	[Fact]
	public void OpenAIResponses_ReturnsNull_WhenSingleInput()
	{
		var body = Parse("""{"input":[{"role":"user","content":"hi"}]}""");

		Assert.Null(CachePrefixHasher.Hash(body, TopHatTarget.OpenAIResponses));
	}

	[Fact]
	public void OpenAIResponses_HashesAllButLastInput()
	{
		var bodyA = Parse("""
			{"input":[
				{"role":"user","content":"q1"},
				{"type":"function_call","call_id":"fc_1","name":"get","arguments":"{}"},
				{"type":"function_call_output","call_id":"fc_1","output":"latest output A"}
			]}
			""");
		var bodyB = Parse("""
			{"input":[
				{"role":"user","content":"q1"},
				{"type":"function_call","call_id":"fc_1","name":"get","arguments":"{}"},
				{"type":"function_call_output","call_id":"fc_1","output":"latest output B"}
			]}
			""");

		Assert.Equal(
			CachePrefixHasher.Hash(bodyA, TopHatTarget.OpenAIResponses),
			CachePrefixHasher.Hash(bodyB, TopHatTarget.OpenAIResponses));
	}

	[Fact]
	public void OpenAIResponses_DetectsPrefixMutation()
	{
		var original = Parse("""
			{"input":[
				{"role":"user","content":"original q1"},
				{"role":"user","content":"latest"}
			]}
			""");
		var mutated = Parse("""
			{"input":[
				{"role":"user","content":"MUTATED q1"},
				{"role":"user","content":"latest"}
			]}
			""");

		Assert.NotEqual(
			CachePrefixHasher.Hash(original, TopHatTarget.OpenAIResponses),
			CachePrefixHasher.Hash(mutated, TopHatTarget.OpenAIResponses));
	}

	#endregion

	[Fact]
	public void UnsupportedTarget_ReturnsNull()
	{
		var body = Parse("""{"messages":[]}""");
		Assert.Null(CachePrefixHasher.Hash(body, TopHatTarget.AnthropicCountTokens));
	}
}
