using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TopHat.Compression.CCR;
using TopHat.DependencyInjection;
using TopHat.Relevance.BM25.DependencyInjection;
using TopHat.Tests.Support;
using TopHat.Transforms.JsonContext;
using Xunit;

namespace TopHat.Tests.Transforms.JsonContext;

public sealed class JsonContextCompressorTransformTests
{
	// ~200 tokens of JSON to clear the min-token gate.
	private static string BigJsonArray(int count = 100) =>
		System.Text.Json.JsonSerializer.Serialize(
			Enumerable.Range(1, count).Select(i =>
				new { id = i, status = "processed", message = "record inserted successfully into database", timestamp = $"2024-01-{i % 28 + 1:D2}" })
			.ToArray());

	private static string ErrorBearingJsonArray(int count = 80) =>
		System.Text.Json.JsonSerializer.Serialize(
			Enumerable.Range(1, count)
				.Select(i => i == 40
					? new { id = i, status = "error", message = "error: disk full, write failed" }
					: new { id = i, status = "ok", message = "record inserted successfully" })
			.ToArray());

	private static HttpRequestMessage AnthropicRequest(string toolResultContent, string userQuery = "list logs") =>
		new(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
		{
			Content = new StringContent($$"""
				{
					"model": "claude-opus-4-5",
					"messages": [
						{ "role": "user", "content": "{{userQuery}}" },
						{ "role": "assistant", "content": [{ "type": "tool_use", "id": "tu_1", "name": "get_logs", "input": {} }] },
						{ "role": "user", "content": [{ "type": "tool_result", "tool_use_id": "tu_1", "content": {{System.Text.Json.JsonSerializer.Serialize(toolResultContent)}} }] }
					],
					"max_tokens": 1000
				}
				""", Encoding.UTF8, "application/json"),
		};

	private static HttpRequestMessage OpenAiChatRequest(string toolResultContent, string userQuery = "list logs") =>
		new(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
		{
			Content = new StringContent($$"""
				{
					"model": "gpt-4o",
					"messages": [
						{ "role": "user", "content": "{{userQuery}}" },
						{ "role": "tool", "tool_call_id": "call_1", "content": {{System.Text.Json.JsonSerializer.Serialize(toolResultContent)}} }
					]
				}
				""", Encoding.UTF8, "application/json"),
		};

	private static (HttpClient Client, Func<Task<JsonObject?>> GetLastBody) Build(Action<JsonContextCompressorOptions>? configureOptions = null)
	{
		JsonObject? captured = null;

		var (client, _, _) = TransformHandlerFactory.Build(
			services =>
			{
				services.AddTopHatBm25Relevance();
				services.AddTopHatJsonContextCompressor(configureOptions);
			},
			behavior: async (req, _) =>
			{
				var bytes = await req.Content!.ReadAsByteArrayAsync();
				captured = JsonNode.Parse(bytes) as JsonObject;
				return new HttpResponseMessage(HttpStatusCode.OK);
			});

		return (client, () => Task.FromResult(captured));
	}

	#region Skip reasons

	[Fact]
	public async Task SmallToolResult_NotCompressed()
	{
		var (client, getBody) = Build();
		// JSON but tiny — below min-token gate.
		var content = """[{"id":1},{"id":2}]""";
		await client.SendAsync(AnthropicRequest(content));

		var received = await getBody();
		var msgs = received!["messages"]!.AsArray();
		var toolResult = msgs[2]!["content"]!.AsArray()[0]!.AsObject();
		Assert.Equal(content, toolResult["content"]!.GetValue<string>());
	}

	[Fact]
	public async Task NonJsonToolResult_PassesThrough()
	{
		var (client, getBody) = Build(o => o.MinTokensToCrush = 1);
		var content = "this is plain text, not JSON at all";
		await client.SendAsync(AnthropicRequest(content));

		var received = await getBody();
		var msgs = received!["messages"]!.AsArray();
		var toolResult = msgs[2]!["content"]!.AsArray()[0]!.AsObject();
		Assert.Equal(content, toolResult["content"]!.GetValue<string>());
	}

	[Fact]
	public async Task NoToolResults_NoMutation()
	{
		var (client, getBody) = Build();
		var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
		{
			Content = new StringContent("""
				{
					"model": "gpt-4o",
					"messages": [
						{ "role": "user", "content": "hello" },
						{ "role": "assistant", "content": "hi" }
					]
				}
				""", Encoding.UTF8, "application/json"),
		};
		await client.SendAsync(request);

		// Should pass through without modification.
		var received = await getBody();
		var msgs = received!["messages"]!.AsArray();
		Assert.Equal(2, msgs.Count);
	}

	#endregion

	#region Compression applied

	[Fact]
	public async Task AnthropicToolResult_LargeJson_Compressed()
	{
		var (client, getBody) = Build();
		var content = BigJsonArray(100);
		await client.SendAsync(AnthropicRequest(content));

		var received = await getBody();
		var msgs = received!["messages"]!.AsArray();
		var toolResult = msgs[2]!["content"]!.AsArray()[0]!.AsObject();
		var compressedContent = toolResult["content"]!.GetValue<string>();

		// Should be compressed — shorter than original.
		Assert.True(compressedContent.Length < content.Length, $"Expected compression: got {compressedContent.Length} vs {content.Length}");
		// Must still be valid JSON.
		var parsed = JsonNode.Parse(compressedContent);
		Assert.IsType<JsonArray>(parsed);
	}

	[Fact]
	public async Task OpenAiChatToolRole_LargeJson_Compressed()
	{
		var (client, getBody) = Build();
		var content = BigJsonArray(100);
		await client.SendAsync(OpenAiChatRequest(content));

		var received = await getBody();
		var msgs = received!["messages"]!.AsArray();
		var toolMsg = msgs[1]!.AsObject();
		var compressedContent = toolMsg["content"]!.GetValue<string>();

		Assert.True(compressedContent.Length < content.Length);
	}

	[Fact]
	public async Task Compress_PreservesErrorItems()
	{
		var (client, getBody) = Build(o => o.MaxItemsAfterCrush = 10);
		var content = ErrorBearingJsonArray(80);
		await client.SendAsync(AnthropicRequest(content, userQuery: "show errors"));

		var received = await getBody();
		var msgs = received!["messages"]!.AsArray();
		var toolResult = msgs[2]!["content"]!.AsArray()[0]!.AsObject();
		var compressedContent = toolResult["content"]!.GetValue<string>();

		Assert.Contains("error", compressedContent, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Compress_FirstAndLastItemsPreserved()
	{
		var (client, getBody) = Build(o => o.MaxItemsAfterCrush = 10);
		var content = BigJsonArray(80);
		await client.SendAsync(AnthropicRequest(content));

		var received = await getBody();
		var msgs = received!["messages"]!.AsArray();
		var toolResult = msgs[2]!["content"]!.AsArray()[0]!.AsObject();
		var compressedContent = toolResult["content"]!.GetValue<string>();
		var compressedArray = JsonNode.Parse(compressedContent)!.AsArray();

		var ids = compressedArray
			.OfType<JsonObject>()
			.Where(obj => obj["id"] is not null)
			.Select(obj => obj["id"]!.GetValue<int>())
			.ToArray();
		Assert.Contains(1, ids);   // First item.
		Assert.Contains(80, ids);  // Last item.
	}

	#endregion

	#region Key order preservation (standing rule #5)

	[Fact]
	public async Task ParseAndReserializeDoesNotReorderKeys()
	{
		// Items with multiple fields in a specific order — compressed output must preserve that order.
		var (client, getBody) = Build(o => o.MinTokensToCrush = 1);

		var items = Enumerable.Range(1, 20).Select(i => new
		{
			zebra = i,
			alpha = $"value_{i}",
			middle = true,
			omega = (double)i,
		}).ToArray();
		var content = System.Text.Json.JsonSerializer.Serialize(items);

		await client.SendAsync(AnthropicRequest(content));

		var received = await getBody();
		var msgs = received!["messages"]!.AsArray();
		var toolResult = msgs[2]!["content"]!.AsArray()[0]!.AsObject();
		var compressedContent = toolResult["content"]!.GetValue<string>();

		var compressedArray = JsonNode.Parse(compressedContent)!.AsArray();
		var firstItem = compressedArray[0]!.AsObject();
		var keys = firstItem.Select(kvp => kvp.Key).ToArray();

		// Keys must appear in their original order: zebra, alpha, middle, omega.
		Assert.Equal(["zebra", "alpha", "middle", "omega"], keys);
	}

	#endregion

	#region DI integration

	[Fact]
	public async Task AddTopHatJsonContextCompressor_RegistersTransformViaDI()
	{
		var (client, getBody) = Build();
		var content = BigJsonArray(100);
		// If registration failed, the request would be forwarded unmodified.
		await client.SendAsync(AnthropicRequest(content));

		var received = await getBody();
		Assert.NotNull(received);
	}

	[Fact]
	public void AddTopHatJsonContextCompressor_WithoutScorer_ThrowsClearSetupError()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddTopHat();
		services.AddTopHatJsonContextCompressor();
		var provider = services.BuildServiceProvider();

		var ex = Assert.Throws<InvalidOperationException>(
			() => provider.GetRequiredService<JsonContextCompressorTransform>());
		Assert.Contains("AddTopHatBm25Relevance", ex.Message, StringComparison.Ordinal);
		Assert.Contains("AddTopHatOnnxRelevance", ex.Message, StringComparison.Ordinal);
	}

	#endregion

	#region CCR integration

	private static (HttpClient Client, Func<Task<JsonObject?>> GetLastBody, ICompressionContextStore Store) BuildWithCCR()
	{
		JsonObject? captured = null;

		var (client, _, services) = TransformHandlerFactory.Build(
			services =>
			{
				services.AddTopHatBm25Relevance();
				services.AddSingleton<IOptions<CCROptions>>(Options.Create(new CCROptions()));
				services.AddSingleton<ICompressionContextStore, InMemoryCompressionContextStore>();
				services.AddTopHatJsonContextCompressor();
			},
			behavior: async (req, _) =>
			{
				var bytes = await req.Content!.ReadAsByteArrayAsync();
				captured = JsonNode.Parse(bytes) as JsonObject;
				return new HttpResponseMessage(HttpStatusCode.OK);
			});

		return (client, () => Task.FromResult(captured), services.GetRequiredService<ICompressionContextStore>());
	}

	[Fact]
	public async Task CCR_Enabled_InjectsRetrievalKeyInMetadata()
	{
		var (client, getBody, _) = BuildWithCCR();
		var content = BigJsonArray(100);
		await client.SendAsync(AnthropicRequest(content));

		var received = await getBody();
		var msgs = received!["messages"]!.AsArray();
		var compressedContent = msgs[2]!["content"]!.AsArray()[0]!["content"]!.GetValue<string>();
		var compressedArray = JsonNode.Parse(compressedContent)!.AsArray();
		var metadata = compressedArray
			.OfType<JsonObject>()
			.Select(obj => obj["_tophat_compression"] as JsonObject)
			.FirstOrDefault(m => m is not null);

		Assert.NotNull(metadata);
		Assert.NotNull(metadata["retrieval_key"]);
		var key = metadata["retrieval_key"]!.GetValue<string>();
		Assert.True(Guid.TryParseExact(key, "N", out _), $"retrieval_key should be a GUID (N format); got '{key}'");
	}

	[Fact]
	public async Task CCR_Enabled_InjectsTophatRetrieveToolIntoRequest()
	{
		var (client, getBody, _) = BuildWithCCR();
		var content = BigJsonArray(100);
		await client.SendAsync(AnthropicRequest(content));

		var received = await getBody();
		var tools = received!["tools"] as JsonArray;

		Assert.NotNull(tools);
		Assert.Contains(tools, node => node is JsonObject obj
			&& obj["name"]?.GetValue<string>() == CCRToolDefinition.ToolName);
	}

	[Fact]
	public async Task CCR_Enabled_StoresDroppedItemsUnderRetrievalKey()
	{
		var (client, getBody, store) = BuildWithCCR();
		var content = BigJsonArray(100);
		await client.SendAsync(AnthropicRequest(content));

		var received = await getBody();
		var msgs = received!["messages"]!.AsArray();
		var compressedContent = msgs[2]!["content"]!.AsArray()[0]!["content"]!.GetValue<string>();
		var compressedArray = JsonNode.Parse(compressedContent)!.AsArray();
		var metadata = compressedArray
			.OfType<JsonObject>()
			.Select(obj => obj["_tophat_compression"] as JsonObject)
			.First(m => m is not null);
		var key = metadata!["retrieval_key"]!.GetValue<string>();

		// Look up an item that's known to have been dropped (IDs > 20 definitely aren't in the
		// kept set for a 100-item array with MaxItemsAfterCrush=15).
		var dropped = store.Retrieve(key, ids: new HashSet<int> { 50 }, limit: 10);

		Assert.Single(dropped);
		Assert.Equal(50, dropped[0]!["id"]!.GetValue<int>());
	}

	[Fact]
	public async Task CCR_Disabled_DoesNotInjectToolOrRetrievalKey()
	{
		// The standard Build() helper does NOT register an ICompressionContextStore, so CCR should
		// be inactive — no retrieval_key in metadata, no tools array added.
		var (client, getBody) = Build();
		var content = BigJsonArray(100);
		await client.SendAsync(AnthropicRequest(content));

		var received = await getBody();
		Assert.Null(received!["tools"]);

		var msgs = received["messages"]!.AsArray();
		var compressedContent = msgs[2]!["content"]!.AsArray()[0]!["content"]!.GetValue<string>();
		var compressedArray = JsonNode.Parse(compressedContent)!.AsArray();
		var metadata = compressedArray
			.OfType<JsonObject>()
			.Select(obj => obj["_tophat_compression"] as JsonObject)
			.FirstOrDefault(m => m is not null);

		if (metadata is not null)
		{
			Assert.Null(metadata["retrieval_key"]);
		}
	}

	[Fact]
	public async Task CCR_OpenAIChatCompletions_InjectsNestedFunctionTool()
	{
		// Chat Completions wraps the function descriptor under `function` with type:"function" at
		// the top level. Verifies the injector emitted the right shape, not just the right name.
		var (client, getBody, _) = BuildWithCCR();
		var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
		{
			Content = new StringContent($$"""
				{
					"model": "gpt-4o",
					"messages": [
						{ "role": "user", "content": "list logs" },
						{ "role": "assistant", "tool_calls": [{ "id": "call_1", "type": "function", "function": { "name": "get_logs", "arguments": "{}" } }] },
						{ "role": "tool", "tool_call_id": "call_1", "content": {{System.Text.Json.JsonSerializer.Serialize(BigJsonArray(100))}} }
					]
				}
				""", Encoding.UTF8, "application/json"),
		};
		await client.SendAsync(request);

		var received = await getBody();
		var tools = received!["tools"] as JsonArray;
		Assert.NotNull(tools);

		var ccrTool = tools.OfType<JsonObject>().FirstOrDefault(t =>
			t["type"]?.GetValue<string>() == "function"
			&& (t["function"] as JsonObject)?["name"]?.GetValue<string>() == CCRToolDefinition.ToolName);

		Assert.NotNull(ccrTool);
		Assert.NotNull(ccrTool["function"]!["parameters"]);
	}

	[Fact]
	public async Task CCR_OpenAIResponses_InjectsFlatFunctionTool()
	{
		// Responses uses a flat tool shape — name, description, parameters at the top level.
		var (client, getBody, _) = BuildWithCCR();
		var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses")
		{
			Content = new StringContent($$"""
				{
					"model": "gpt-4o",
					"input": [
						{ "role": "user", "content": "list logs" },
						{ "type": "function_call", "call_id": "call_1", "name": "get_logs", "arguments": "{}" },
						{ "type": "function_call_output", "call_id": "call_1", "output": {{System.Text.Json.JsonSerializer.Serialize(BigJsonArray(100))}} }
					]
				}
				""", Encoding.UTF8, "application/json"),
		};
		await client.SendAsync(request);

		var received = await getBody();
		var tools = received!["tools"] as JsonArray;
		Assert.NotNull(tools);

		var ccrTool = tools.OfType<JsonObject>().FirstOrDefault(t =>
			t["type"]?.GetValue<string>() == "function"
			&& t["name"]?.GetValue<string>() == CCRToolDefinition.ToolName);

		Assert.NotNull(ccrTool);
		Assert.NotNull(ccrTool["parameters"]);
		// Flat shape: should NOT have nested function field.
		Assert.Null(ccrTool["function"]);
	}

	#endregion
}
