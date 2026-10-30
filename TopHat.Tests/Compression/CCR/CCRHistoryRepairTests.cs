using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TopHat.Compression.CCR.DependencyInjection;
using TopHat.DependencyInjection;
using TopHat.Providers;
using TopHat.Relevance.BM25.DependencyInjection;
using TopHat.Tests.Support;
using TopHat.Transforms;
using Xunit;

namespace TopHat.Tests.Compression.CCR;

/// <summary>Exercises history recovery at the public HTTP handler boundary.</summary>
public sealed class CCRHistoryRepairTests
{
	/// <summary>Unanswered internal calls are repaired without altering caller tools or text.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task MissingResults_RepairsMultipleInternalCallsAndPreservesCallerContent(TopHatTarget target)
	{
		var body = CreateMixedHistory(target);
		var callerBefore = CallerObjects(body, target);
		var repaired = JsonNode.Parse(await ForwardAsync(target, body.ToJsonString()))!.AsObject();

		AssertInternalPairs(repaired, target, "internal-first", "internal-second");
		Assert.Equal(callerBefore, CallerObjects(repaired, target));
		Assert.Contains("keep this user text", repaired.ToJsonString(), StringComparison.Ordinal);
		Assert.Contains("keep this assistant text", repaired.ToJsonString(), StringComparison.Ordinal);
	}

	/// <summary>Existing internal results are reused and duplicate or misplaced results are normalized.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task DuplicateMisplacedResults_RepairsPairingAndRetainsExistingPayload(TopHatTarget target)
	{
		var body = CreateMixedHistory(target);
		AppendResult(body, target, "internal-first", "existing recovered payload");
		AppendResult(body, target, "internal-first", "duplicate payload");
		AppendResult(body, target, "internal-second", "second recovered payload");
		var callerBefore = CallerObjects(body, target);
		var repaired = JsonNode.Parse(await ForwardAsync(target, body.ToJsonString()))!.AsObject();

		AssertInternalPairs(repaired, target, "internal-first", "internal-second");
		Assert.Equal(callerBefore, CallerObjects(repaired, target));
		Assert.Contains("existing recovered payload", repaired.ToJsonString(), StringComparison.Ordinal);
		Assert.Contains("second recovered payload", repaired.ToJsonString(), StringComparison.Ordinal);
		Assert.DoesNotContain("duplicate payload", repaired.ToJsonString(), StringComparison.Ordinal);
		AssertNoEmptyMessages(repaired, target);
	}

	/// <summary>Malformed and duplicate internal calls cannot leave empty messages or unmatched results.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task MalformedAndDuplicateCalls_RemoveOnlyUnpairableInternalContent(TopHatTarget target)
	{
		var body = CreateMixedHistory(target);
		AppendCall(body, target, CreateCall(target, "internal-first", "tophat_retrieve"));
		AppendResult(body, target, "internal-first", "one recovered payload");
		var malformed = CreateCall(target, "unused", "tophat_retrieve");
		malformed.Remove(CallIdProperty(target));
		AppendCall(body, target, malformed);
		var wrongType = CreateCall(target, "unused", "tophat_retrieve");
		wrongType[CallIdProperty(target)] = 42;
		AppendCall(body, target, wrongType);
		var callerBefore = CallerObjects(body, target);
		var repaired = JsonNode.Parse(await ForwardAsync(target, body.ToJsonString()))!.AsObject();

		AssertInternalPairs(repaired, target, "internal-first", "internal-second");
		Assert.Equal(2, Calls(repaired, target).Count(IsInternal));
		Assert.Equal(callerBefore, CallerObjects(repaired, target));
		AssertNoEmptyMessages(repaired, target);
	}

	/// <summary>Recovery is independent of compressor presence and the mutable compression window.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages, false)]
	[InlineData(TopHatTarget.AnthropicMessages, true)]
	[InlineData(TopHatTarget.OpenAIChatCompletions, false)]
	[InlineData(TopHatTarget.OpenAIChatCompletions, true)]
	[InlineData(TopHatTarget.OpenAIResponses, false)]
	[InlineData(TopHatTarget.OpenAIResponses, true)]
	public async Task OldHistory_IsRepairedWhenCompressorAbsentOrHasNoEligibleResults(TopHatTarget target, bool compressorEnabled)
	{
		var body = CreateMixedHistory(target);
		Transcript(body, target).Add(new JsonObject { ["role"] = "user", ["content"] = "new question without tool data" });
		var repaired = JsonNode.Parse(await ForwardAsync(target, body.ToJsonString(), compressorEnabled))!.AsObject();

		AssertInternalPairs(repaired, target, "internal-first", "internal-second");
	}

	/// <summary>Streaming requests still recover previously leaked history.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task StreamingHistory_IsRepairedBeforeUpstreamSend(TopHatTarget target)
	{
		var body = CreateMixedHistory(target);
		body["stream"] = true;
		var repaired = JsonNode.Parse(await ForwardAsync(target, body.ToJsonString(), compressorEnabled: true))!.AsObject();

		AssertInternalPairs(repaired, target, "internal-first", "internal-second");
		Assert.True(repaired["stream"]!.GetValue<bool>());
	}

	/// <summary>Fail-open rollback by another transform cannot undo required CCR history repair.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task LaterFailOpenTransform_DoesNotEraseRepair(TopHatTarget target)
	{
		var body = CreateMixedHistory(target);
		var repaired = JsonNode.Parse(await ForwardAsync(target, body.ToJsonString(), failingTransform: true))!.AsObject();

		AssertInternalPairs(repaired, target, "internal-first", "internal-second");
	}

	/// <summary>Already valid histories retain the original serialized bytes.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task ValidHistory_IsBytePreserved(TopHatTarget target)
	{
		var body = CreateValidHistory(target);
		var original = "  \n" + body.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n ";

		Assert.Equal(original, await ForwardAsync(target, original));
	}

	/// <summary>A repaired request is stable when forwarded again.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task RepairedHistory_IsIdempotent(TopHatTarget target)
	{
		var first = await ForwardAsync(target, CreateMixedHistory(target).ToJsonString());
		AssertInternalPairs(JsonNode.Parse(first)!.AsObject(), target, "internal-first", "internal-second");

		Assert.Equal(first, await ForwardAsync(target, first));
	}

	/// <summary>Unmatched IDs do not establish that a result belongs to TopHat.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task UnknownOrphanResults_ArePreservedEvenWithInternalLookingIds(TopHatTarget target)
	{
		var body = CreateMixedHistory(target);
		AppendResult(body, target, "tophat_retrieve_unknown", "caller-owned opaque output");
		var originalUnknown = Results(body, target).Single(result => ResultId(result, target) == "tophat_retrieve_unknown").ToJsonString();
		var repaired = JsonNode.Parse(await ForwardAsync(target, body.ToJsonString()))!.AsObject();

		AssertInternalPairs(repaired, target, "internal-first", "internal-second");
		Assert.Equal(originalUnknown, Results(repaired, target).Single(result => ResultId(result, target) == "tophat_retrieve_unknown").ToJsonString());
	}

	/// <summary>A reused caller ID must never cause its output to be claimed by CCR.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task InternalCallSharingCallerId_PreservesCallerPair(TopHatTarget target)
	{
		var body = CreateMixedHistory(target);
		Calls(body, target).First(IsInternal)[CallIdProperty(target)] = "caller-one";
		var callerBefore = CallerObjects(body, target);
		var repaired = JsonNode.Parse(await ForwardAsync(target, body.ToJsonString()))!.AsObject();

		AssertInternalPairs(repaired, target, "internal-second");
		Assert.Single(Calls(repaired, target), IsInternal);
		Assert.Equal(callerBefore, CallerObjects(repaired, target));
		Assert.Single(Results(repaired, target), result => ResultId(result, target) == "caller-one");
	}

	/// <summary>Tool-shaped caller data outside an assistant tool-call slot is not internal history.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task InternalLookingDataInWrongRole_IsPreserved(TopHatTarget target)
	{
		var body = CreateMixedHistory(target);
		var data = new JsonObject { ["role"] = "user", ["content"] = "caller data, not an assistant call" };
		data[target == TopHatTarget.OpenAIChatCompletions ? "tool_calls" : "content"] = new JsonArray(CreateCall(target, "wrong-role-id", "tophat_retrieve"));
		Transcript(body, target).Add(data);
		var originalData = data.ToJsonString();
		var repaired = JsonNode.Parse(await ForwardAsync(target, body.ToJsonString()))!.AsObject();

		AssertInternalPairs(repaired, target, "internal-first", "internal-second");
		Assert.Equal(originalData, Transcript(repaired, target)[^1]!.ToJsonString());
		Assert.DoesNotContain(Results(repaired, target), result => ResultId(result, target) == "wrong-role-id");
	}

	/// <summary>Removing an invalid named call also removes only its proven malformed output.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task EmptyInternalId_RemovesItsKnownOutputWithoutLeavingEmptyMessages(TopHatTarget target)
	{
		var body = CreateMixedHistory(target);
		AppendCall(body, target, CreateCall(target, string.Empty, "tophat_retrieve"));
		AppendResult(body, target, string.Empty, "invalid internal output");
		var repaired = JsonNode.Parse(await ForwardAsync(target, body.ToJsonString()))!.AsObject();

		AssertInternalPairs(repaired, target, "internal-first", "internal-second");
		Assert.Equal(2, Calls(repaired, target).Count(IsInternal));
		Assert.DoesNotContain("invalid internal output", repaired.ToJsonString(), StringComparison.Ordinal);
		AssertNoEmptyMessages(repaired, target);
	}

	/// <summary>CCR-disabled histories remain entirely under caller control.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task CcrDisabled_LeavesHistoryUnchanged(TopHatTarget target)
	{
		var original = CreateMixedHistory(target).ToJsonString();
		Assert.Equal(original, await ForwardAsync(target, original, ccrEnabled: false));
	}

	private static async Task<string> ForwardAsync(TopHatTarget target, string body, bool compressorEnabled = false, bool failingTransform = false, bool ccrEnabled = true)
	{
		string? captured = null;
		var (client, _, provider) = TransformHandlerFactory.Build(services =>
		{
			if (ccrEnabled)
			{
				services.AddTopHatCCR();
			}
			if (compressorEnabled)
			{
				services.AddTopHatBm25Relevance();
				services.AddTopHatJsonContextCompressor(options => options.UnfrozenMessageCount = 1);
			}
			if (failingTransform)
			{
				services.AddTopHatRequestTransform<FailingHistoryTransform>(options => options.WithOrder(int.MaxValue));
			}
		}, behavior: async (request, cancellationToken) =>
		{
			captured = await request.Content!.ReadAsStringAsync(cancellationToken);
			return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
		});

		using ((IDisposable)provider)
		using (client)
		using (var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(target)) { Content = new StringContent(body, Encoding.UTF8, "application/json") })
		using (var response = await client.SendAsync(request))
		{
			Assert.True(response.IsSuccessStatusCode);
			return Assert.IsType<string>(captured);
		}
	}

	private static JsonObject CreateMixedHistory(TopHatTarget target)
	{
		var body = new JsonObject { ["model"] = "test-model", ["max_tokens"] = 100 };
		var transcript = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "initial question" } };
		body[target == TopHatTarget.OpenAIResponses ? "input" : "messages"] = transcript;
		var calls = new JsonArray
		{
			CreateCall(target, "internal-first", "tophat_retrieve"),
			CreateCall(target, "caller-one", "caller_tool"),
			CreateCall(target, "internal-second", "tophat_retrieve"),
			CreateCall(target, "caller-two", "other_tool"),
		};

		switch (target)
		{
			case TopHatTarget.AnthropicMessages:
				calls.Insert(1, new JsonObject { ["type"] = "text", ["text"] = "keep this assistant text" });
				transcript.Add(new JsonObject { ["role"] = "assistant", ["content"] = calls });
				transcript.Add(new JsonObject
				{
					["role"] = "user",
					["content"] = new JsonArray(CreateResult(target, "caller-one", "caller result one"), CreateResult(target, "caller-two", "caller result two"),
						new JsonObject { ["type"] = "text", ["text"] = "keep this user text" }),
				});
				break;
			case TopHatTarget.OpenAIChatCompletions:
				transcript.Add(new JsonObject { ["role"] = "assistant", ["content"] = "keep this assistant text", ["tool_calls"] = calls });
				transcript.Add(CreateResult(target, "caller-one", "caller result one"));
				transcript.Add(CreateResult(target, "caller-two", "caller result two"));
				transcript.Add(new JsonObject { ["role"] = "user", ["content"] = "keep this user text" });
				break;
			default:
				foreach (var call in calls)
				{
					transcript.Add(call!.DeepClone());
				}
				transcript.Add(new JsonObject { ["role"] = "assistant", ["content"] = "keep this assistant text" });
				transcript.Add(CreateResult(target, "caller-one", "caller result one"));
				transcript.Add(CreateResult(target, "caller-two", "caller result two"));
				transcript.Add(new JsonObject { ["role"] = "user", ["content"] = "keep this user text" });
				break;
		}
		return body;
	}

	private static JsonObject CreateValidHistory(TopHatTarget target)
	{
		var body = CreateMixedHistory(target);
		var transcript = Transcript(body, target);
		if (target == TopHatTarget.AnthropicMessages)
		{
			var results = transcript[2]!["content"]!.AsArray();
			results.Insert(0, CreateResult(target, "internal-first", "existing output one"));
			results.Insert(1, CreateResult(target, "internal-second", "existing output two"));
		}
		else if (target == TopHatTarget.OpenAIChatCompletions)
		{
			transcript.Insert(2, CreateResult(target, "internal-first", "existing output one"));
			transcript.Insert(3, CreateResult(target, "internal-second", "existing output two"));
		}
		else
		{
			AppendResult(body, target, "internal-first", "existing output one");
			AppendResult(body, target, "internal-second", "existing output two");
		}
		return body;
	}

	private static JsonObject CreateCall(TopHatTarget target, string id, string name) => target switch
	{
		TopHatTarget.AnthropicMessages => new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = new JsonObject { ["preserve"] = true } },
		TopHatTarget.OpenAIChatCompletions => new JsonObject { ["type"] = "function", ["id"] = id, ["function"] = new JsonObject { ["name"] = name, ["arguments"] = "{\"preserve\":true}" } },
		_ => new JsonObject { ["type"] = "function_call", ["call_id"] = id, ["name"] = name, ["arguments"] = "{\"preserve\":true}" },
	};

	private static JsonObject CreateResult(TopHatTarget target, string id, string output) => target switch
	{
		TopHatTarget.AnthropicMessages => new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = output },
		TopHatTarget.OpenAIChatCompletions => new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = output },
		_ => new JsonObject { ["type"] = "function_call_output", ["call_id"] = id, ["output"] = output },
	};

	private static void AppendResult(JsonObject body, TopHatTarget target, string id, string output)
	{
		var result = CreateResult(target, id, output);
		Transcript(body, target).Add(target == TopHatTarget.AnthropicMessages ? new JsonObject { ["role"] = "user", ["content"] = new JsonArray(result) } : result);
	}

	private static void AppendCall(JsonObject body, TopHatTarget target, JsonObject call)
	{
		Transcript(body, target).Add(target switch
		{
			TopHatTarget.AnthropicMessages => new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(call) },
			TopHatTarget.OpenAIChatCompletions => new JsonObject { ["role"] = "assistant", ["content"] = null, ["tool_calls"] = new JsonArray(call) },
			_ => call,
		});
	}

	private static void AssertInternalPairs(JsonObject body, TopHatTarget target, params string[] ids)
	{
		var transcript = Transcript(body, target);
		foreach (var id in ids)
		{
			var call = Assert.Single(Calls(body, target), call => IsInternal(call) && call[CallIdProperty(target)]?.GetValue<string>() == id);
			var result = Assert.Single(Results(body, target), result => ResultId(result, target) == id);
			if (target == TopHatTarget.AnthropicMessages)
			{
				var callMessage = (JsonObject)call.Parent!.Parent!;
				var following = Assert.IsType<JsonObject>(transcript[transcript.IndexOf(callMessage) + 1]);
				Assert.Equal("user", following["role"]?.GetValue<string>());
				var content = following["content"]!.AsArray();
				Assert.Contains(result, content);
				Assert.All(content.Take(content.IndexOf(result)), block => Assert.Equal("tool_result", block!["type"]?.GetValue<string>()));
			}
			else if (target == TopHatTarget.OpenAIChatCompletions)
			{
				var callMessage = (JsonObject)call.Parent!.Parent!;
				var callIndex = transcript.IndexOf(callMessage);
				var resultIndex = transcript.IndexOf(result);
				Assert.True(resultIndex > callIndex);
				Assert.All(transcript.Skip(callIndex + 1).Take(resultIndex - callIndex), message => Assert.Equal("tool", message!["role"]?.GetValue<string>()));
			}
			else
			{
				Assert.True(transcript.IndexOf(result) > transcript.IndexOf(call));
			}
		}
	}

	private static void AssertNoEmptyMessages(JsonObject body, TopHatTarget target)
	{
		foreach (var message in Transcript(body, target).OfType<JsonObject>())
		{
			if (message["content"] is JsonArray content)
			{
				Assert.NotEmpty(content);
			}
			if (target == TopHatTarget.OpenAIChatCompletions && message["role"]?.GetValue<string>() == "assistant")
			{
				Assert.True(message["content"] is not null || message["tool_calls"] is JsonArray { Count: > 0 });
			}
		}
	}

	private static string[] CallerObjects(JsonObject body, TopHatTarget target) => Calls(body, target).Where(call => !IsInternal(call)).Concat(Results(body, target)
		.Where(result => ResultId(result, target)?.StartsWith("caller-", StringComparison.Ordinal) == true)).Select(item => item.ToJsonString()).ToArray();

	private static IEnumerable<JsonObject> Calls(JsonObject body, TopHatTarget target) => target switch
	{
		TopHatTarget.AnthropicMessages => Transcript(body, target).OfType<JsonObject>().SelectMany(message => (message["content"] as JsonArray)?.OfType<JsonObject>() ?? [])
			.Where(block => block["type"]?.GetValue<string>() == "tool_use"),
		TopHatTarget.OpenAIChatCompletions => Transcript(body, target).OfType<JsonObject>().SelectMany(message => (message["tool_calls"] as JsonArray)?.OfType<JsonObject>() ?? []),
		_ => Transcript(body, target).OfType<JsonObject>().Where(item => item["type"]?.GetValue<string>() == "function_call"),
	};

	private static IEnumerable<JsonObject> Results(JsonObject body, TopHatTarget target) => target switch
	{
		TopHatTarget.AnthropicMessages => Transcript(body, target).OfType<JsonObject>().SelectMany(message => (message["content"] as JsonArray)?.OfType<JsonObject>() ?? [])
			.Where(block => block["type"]?.GetValue<string>() == "tool_result"),
		TopHatTarget.OpenAIChatCompletions => Transcript(body, target).OfType<JsonObject>().Where(message => message["role"]?.GetValue<string>() == "tool"),
		_ => Transcript(body, target).OfType<JsonObject>().Where(item => item["type"]?.GetValue<string>() == "function_call_output"),
	};

	private static bool IsInternal(JsonObject call) => (call["name"] ?? call["function"]?["name"])?.GetValue<string>() == "tophat_retrieve";
	private static string CallIdProperty(TopHatTarget target) => target == TopHatTarget.OpenAIResponses ? "call_id" : "id";
	private static string? ResultId(JsonObject result, TopHatTarget target) => result[target switch
	{
		TopHatTarget.AnthropicMessages => "tool_use_id",
		TopHatTarget.OpenAIChatCompletions => "tool_call_id",
		_ => "call_id",
	}]?.GetValue<string>();
	private static JsonArray Transcript(JsonObject body, TopHatTarget target) => body[target == TopHatTarget.OpenAIResponses ? "input" : "messages"]!.AsArray();
	private static string Endpoint(TopHatTarget target) => target switch
	{
		TopHatTarget.AnthropicMessages => "https://api.anthropic.com/v1/messages",
		TopHatTarget.OpenAIChatCompletions => "https://api.openai.com/v1/chat/completions",
		_ => "https://api.openai.com/v1/responses",
	};

	/// <summary>A later optional transform that forces the pipeline's rollback behavior.</summary>
	public sealed class FailingHistoryTransform : IRequestTransform
	{
		/// <inheritdoc/>
		public ValueTask InvokeAsync(RequestTransformContext context, CancellationToken cancellationToken) => throw new InvalidOperationException("Intentional fail-open regression fixture.");
	}
}
