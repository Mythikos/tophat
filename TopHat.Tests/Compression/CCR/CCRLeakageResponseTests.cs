using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TopHat.Compression.CCR;
using TopHat.Providers;
using TopHat.Tests.Support;
using Xunit;

namespace TopHat.Tests.Compression.CCR;

/// <summary>Contract tests for keeping TopHat-owned calls inside the response orchestration boundary.</summary>
public sealed class CCRLeakageResponseTests
{
	/// <summary>Mixed turns belong to the caller; only synthetic calls are removed and nothing is re-dispatched.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task MixedCalls_RemoveOnlyRetrievalCallsAndPreserveCallerPayload(TopHatTarget target)
	{
		var orchestrator = CreateOrchestrator(target);
		var first = Call(target, "foreign_1", "first_tool", new JsonObject { ["query"] = "keep this" });
		var second = Call(target, "foreign_2", "second_tool", new JsonObject { ["count"] = 4 });
		var original = ResponseBody(target, first, Call(target, "internal_1"), second, Call(target, "internal_2"));
		var expected = ResponseBody(target, first.DeepClone().AsObject(), second.DeepClone().AsObject());
		var dispatchCount = 0;

		using (var request = Request(target))
		using (var initial = JsonResponse(original))
		{
			initial.Headers.TryAddWithoutValidation("x-request-id", "upstream-request");
			initial.Content.Headers.TryAddWithoutValidation("x-content-marker", "keep");
			var context = Context(request, initial, (_, _) =>
			{
				dispatchCount++;
				throw new InvalidOperationException("Mixed calls must return to the caller without dispatch.");
			});
			using (var result = await orchestrator.OrchestrateAsync(context, CancellationToken.None))
			{
				var actual = await ReadBodyAsync(result);
				Assert.Equal(0, dispatchCount);
				Assert.True(JsonNode.DeepEquals(expected, actual), actual.ToJsonString());
				Assert.Equal("upstream-request", result.Headers.GetValues("x-request-id").Single());
				Assert.Equal("keep", result.Content.Headers.GetValues("x-content-marker").Single());
				Assert.Equal((await result.Content.ReadAsByteArrayAsync()).LongLength, result.Content.Headers.ContentLength);
				Assert.False(result.Headers.Contains(CCRUsageMerger.HopCountHeader));
			}
		}
	}

	/// <summary>The retrieval allowance is followed by exactly one exhaustion completion, including a zero allowance.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages, 0)]
	[InlineData(TopHatTarget.AnthropicMessages, 1)]
	[InlineData(TopHatTarget.AnthropicMessages, 2)]
	[InlineData(TopHatTarget.OpenAIChatCompletions, 0)]
	[InlineData(TopHatTarget.OpenAIChatCompletions, 1)]
	[InlineData(TopHatTarget.OpenAIChatCompletions, 2)]
	[InlineData(TopHatTarget.OpenAIResponses, 0)]
	[InlineData(TopHatTarget.OpenAIResponses, 1)]
	[InlineData(TopHatTarget.OpenAIResponses, 2)]
	public async Task Exhaustion_PerformsBoundedFinalCompletionAndSanitizesRepeatedCalls(TopHatTarget target, int allowance)
	{
		var orchestrator = CreateOrchestrator(target, allowance);
		var captured = new List<JsonObject>();
		using (var request = Request(target))
		using (var initial = JsonResponse(ResponseBody(target, Call(target, "internal_0_a"), Call(target, "internal_0_b"))))
		{
			var context = Context(request, initial, async (followUp, cancellationToken) =>
			{
				captured.Add(JsonNode.Parse(await followUp.Content!.ReadAsStringAsync(cancellationToken))!.AsObject());
				Assert.True(captured.Count <= allowance + 1, "The exhaustion completion must never restart retrieval.");
				return JsonResponse(ResponseBody(target, Call(target, $"internal_{captured.Count}_a"), Call(target, $"internal_{captured.Count}_b")));
			});
			using (var result = await orchestrator.OrchestrateAsync(context, CancellationToken.None))
			{
				Assert.Equal(allowance + 1, captured.Count);
				for (var hop = 0; hop < captured.Count; hop++)
				{
					AssertLastRoundPairing(target, captured[hop], $"internal_{hop}_a", $"internal_{hop}_b");
					var results = Results(target, captured[hop]);
					Assert.Equal((hop + 1) * 2, results.Count);
					foreach (var suffix in new[] { "a", "b" })
					{
						var id = $"internal_{hop}_{suffix}";
						var paired = Assert.Single(results, item => item.Id == id);
						if (hop < allowance)
						{
							var retrieved = JsonNode.Parse(paired.Content)!.AsArray();
							Assert.Single(retrieved);
							Assert.Equal(7, retrieved[0]!["id"]!.GetValue<int>());
						}
						else
						{
							Assert.Contains("budget", paired.Content, StringComparison.OrdinalIgnoreCase);
							Assert.Contains("exhaust", paired.Content, StringComparison.OrdinalIgnoreCase);
						}
					}
				}
				Assert.DoesNotContain(captured[^1]["tools"]!.AsArray(), tool => ToolName(target, tool!.AsObject()) == CCRToolDefinition.ToolName);
				Assert.Equal(new[] { "first_tool", "second_tool" }, captured[^1]["tools"]!.AsArray().Select(tool => ToolName(target, tool!.AsObject())));
				var body = await ReadBodyAsync(result);
				Assert.Empty(Calls(target, body));
				Assert.Equal(target == TopHatTarget.AnthropicMessages ? "end_turn" : target == TopHatTarget.OpenAIChatCompletions ? "stop" : "completed", StopReason(target, body));
				Assert.Equal((allowance + 2).ToString(CultureInfo.InvariantCulture), result.Headers.GetValues(CCRUsageMerger.HopCountHeader).Single());
				Assert.Equal((allowance + 2) * 100, body["usage"]![InputUsageField(target)]!.GetValue<int>());
				Assert.Equal((allowance + 2) * 20, body["usage"]![OutputUsageField(target)]!.GetValue<int>());
				Assert.Equal((allowance + 2) * 10, body["usage"]!["input_tokens_details"]!["cached_tokens"]!.GetValue<int>());
			}
		}
	}

	/// <summary>A mixed turn takes precedence over budget exhaustion and is never re-dispatched.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages, false)]
	[InlineData(TopHatTarget.AnthropicMessages, true)]
	[InlineData(TopHatTarget.OpenAIChatCompletions, false)]
	[InlineData(TopHatTarget.OpenAIChatCompletions, true)]
	[InlineData(TopHatTarget.OpenAIResponses, false)]
	[InlineData(TopHatTarget.OpenAIResponses, true)]
	public async Task MixedCallsAtBudgetBoundary_ReturnWithoutExhaustionDispatch(TopHatTarget target, bool afterRetrieval)
	{
		var mixed = ResponseBody(target, Call(target, "internal_mixed"), Call(target, "foreign_mixed", "first_tool"));
		var count = 0;
		using (var request = Request(target))
		using (var initial = JsonResponse(afterRetrieval ? ResponseBody(target, Call(target, "internal_0")) : mixed))
		{
			var context = Context(request, initial, (_, _) =>
			{
				count++;
				Assert.True(afterRetrieval && count == 1, "Mixed calls must not trigger an exhaustion completion.");
				return Task.FromResult(JsonResponse(mixed));
			});
			using (var result = await CreateOrchestrator(target, afterRetrieval ? 1 : 0).OrchestrateAsync(context, CancellationToken.None))
			{
				Assert.Equal(afterRetrieval ? 1 : 0, count);
				var foreign = Assert.Single(Calls(target, await ReadBodyAsync(result)));
				Assert.Equal("foreign_mixed", foreign[target == TopHatTarget.OpenAIResponses ? "call_id" : "id"]!.GetValue<string>());
			}
		}
	}

	/// <summary>An internal-only final response remains terminal even when removing it leaves no visible text.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task ExhaustionFinalInternalOnlyTurn_DoesNotRequireTextToSanitize(TopHatTarget target)
	{
		var body = ResponseBody(target, Call(target, "internal_only"));
		if (target == TopHatTarget.OpenAIChatCompletions)
		{
			body["choices"]![0]!["message"]!["content"] = null;
		}
		else
		{
			body[target == TopHatTarget.AnthropicMessages ? "content" : "output"]!.AsArray().RemoveAt(0);
		}
		var count = 0;
		using (var request = Request(target))
		using (var initial = JsonResponse(body))
		{
			var context = Context(request, initial, (_, _) =>
			{
				count++;
				Assert.Equal(1, count);
				return Task.FromResult(JsonResponse(body));
			});
			using (var result = await CreateOrchestrator(target, 0).OrchestrateAsync(context, CancellationToken.None))
			{
				Assert.Equal(1, count);
				var final = await ReadBodyAsync(result);
				Assert.Empty(Calls(target, final));
				Assert.Equal(target == TopHatTarget.AnthropicMessages ? "end_turn" : target == TopHatTarget.OpenAIChatCompletions ? "stop" : "completed", StopReason(target, final));
			}
		}
	}

	/// <summary>Completion at the retrieval limit does not incur an unnecessary exhaustion call.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task AnswerAtRetrievalLimit_DoesNotDispatchExhaustionCompletion(TopHatTarget target)
	{
		var count = 0;
		using (var request = Request(target))
		using (var initial = JsonResponse(ResponseBody(target, Call(target, "internal_0"))))
		{
			var context = Context(request, initial, (_, _) =>
			{
				count++;
				return Task.FromResult(JsonResponse(ResponseBody(target)));
			});
			using (var result = await CreateOrchestrator(target, 1).OrchestrateAsync(context, CancellationToken.None))
			{
				Assert.Equal(1, count);
				Assert.Empty(Calls(target, await ReadBodyAsync(result)));
				Assert.Equal("2", result.Headers.GetValues(CCRUsageMerger.HopCountHeader).Single());
			}
		}
	}

	/// <summary>A mixed final completion still returns every caller-owned action after internal calls are removed.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task ExhaustionFinalMixedTurn_PreservesForeignCallAndStopReason(TopHatTarget target)
	{
		var count = 0;
		var foreign = Call(target, "foreign_final", "first_tool", new JsonObject { ["value"] = "unchanged" });
		using (var request = Request(target))
		using (var initial = JsonResponse(ResponseBody(target, Call(target, "internal_0"))))
		{
			var context = Context(request, initial, (_, _) =>
			{
				count++;
				return Task.FromResult(JsonResponse(ResponseBody(target, Call(target, "internal_final"), foreign.DeepClone().AsObject())));
			});
			using (var result = await CreateOrchestrator(target, 0).OrchestrateAsync(context, CancellationToken.None))
			{
				Assert.Equal(1, count);
				var body = await ReadBodyAsync(result);
				Assert.True(JsonNode.DeepEquals(foreign, Assert.Single(Calls(target, body))));
				Assert.Equal(target == TopHatTarget.AnthropicMessages ? "tool_use" : target == TopHatTarget.OpenAIChatCompletions ? "tool_calls" : "completed", StopReason(target, body));
			}
		}
	}

	/// <summary>Filtering a Responses output cannot establish a different server-side response status.</summary>
	[Theory]
	[InlineData("in_progress")]
	[InlineData("incomplete")]
	[InlineData("failed")]
	[InlineData("cancelled")]
	public async Task ResponsesTerminalSanitization_PreservesProviderStatus(string status)
	{
		const TopHatTarget target = TopHatTarget.OpenAIResponses;
		var body = ResponseBody(target, Call(target, "internal_status"));
		body["status"] = status;
		body["incomplete_details"] = new JsonObject { ["reason"] = "max_output_tokens" };
		using (var request = Request(target))
		using (var initial = JsonResponse(ResponseBody(target, Call(target, "internal_initial"))))
		{
			var context = Context(request, initial, (_, _) => Task.FromResult(JsonResponse(body)));
			using (var result = await CreateOrchestrator(target, 0).OrchestrateAsync(context, CancellationToken.None))
			{
				var final = await ReadBodyAsync(result);
				Assert.Empty(Calls(target, final));
				Assert.Equal(status, final["status"]!.GetValue<string>());
				Assert.True(JsonNode.DeepEquals(body["incomplete_details"], final["incomplete_details"]));
			}
		}
	}

	/// <summary>A same-name custom call is caller-owned even beside an internal function call.</summary>
	[Theory]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task MixedCustomCall_PreservesNonFunctionCallWithSameName(TopHatTarget target)
	{
		var custom = target == TopHatTarget.OpenAIChatCompletions
			? new JsonObject { ["type"] = "custom", ["id"] = "caller_custom", ["custom"] = new JsonObject { ["name"] = CCRToolDefinition.ToolName, ["input"] = "caller payload" }, ["function"] = new JsonObject { ["name"] = CCRToolDefinition.ToolName } }
			: new JsonObject { ["type"] = "custom_tool_call", ["id"] = "ctc_custom", ["call_id"] = "caller_custom", ["name"] = CCRToolDefinition.ToolName, ["input"] = "caller payload" };
		using (var request = Request(target))
		using (var initial = JsonResponse(ResponseBody(target, Call(target, "internal_function"), custom)))
		{
			var context = Context(request, initial, (_, _) => throw new InvalidOperationException("Caller-owned custom calls must be returned without dispatch."));
			using (var result = await CreateOrchestrator(target).OrchestrateAsync(context, CancellationToken.None))
			{
				var body = await ReadBodyAsync(result);
				var items = target == TopHatTarget.OpenAIChatCompletions ? body["choices"]![0]!["message"]!["tool_calls"]!.AsArray() : body["output"]!.AsArray();
				var remaining = Assert.Single(items.OfType<JsonObject>(), item => item["type"]!.GetValue<string>() is "custom" or "custom_tool_call");
				Assert.True(JsonNode.DeepEquals(custom, remaining));
				Assert.DoesNotContain(items.OfType<JsonObject>(), item => item["type"]!.GetValue<string>() is "function" or "function_call");
			}
		}
	}

	/// <summary>Reused provider IDs are remapped so each new retrieval and the final exhaustion result remain paired.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task ReusedCallIds_PreserveEachRetrievalAndFinalExhaustionResult(TopHatTarget target)
	{
		var count = 0;
		using (var request = Request(target))
		using (var initial = JsonResponse(ResponseBody(target, Call(target, "reused_id"))))
		{
			var context = Context(request, initial, async (followUp, _) =>
			{
				count++;
				var body = JsonNode.Parse(await followUp.Content!.ReadAsStringAsync())!.AsObject();
				var results = Results(target, body);
				Assert.Equal(count, results.Count);
				Assert.Equal(count, results.Select(result => result.Id).Distinct(StringComparer.Ordinal).Count());
				Assert.Equal("reused_id", results[0].Id);
				Assert.Equal(7, JsonNode.Parse(results[0].Content)![0]!["id"]!.GetValue<int>());
				if (count >= 2)
				{
					Assert.Equal(42, JsonNode.Parse(results[1].Content)![0]!["id"]!.GetValue<int>());
				}
				if (count == 3)
				{
					Assert.Contains("budget", results[2].Content, StringComparison.OrdinalIgnoreCase);
					Assert.Contains("exhaust", results[2].Content, StringComparison.OrdinalIgnoreCase);
				}
				var transcript = body[target == TopHatTarget.OpenAIResponses ? "input" : "messages"]!.AsArray();
				var calls = target == TopHatTarget.AnthropicMessages
					? transcript.OfType<JsonObject>().Where(item => item["content"] is JsonArray).SelectMany(item => item["content"]!.AsArray().OfType<JsonObject>()).Where(item => item["type"]?.GetValue<string>() == "tool_use")
					: target == TopHatTarget.OpenAIChatCompletions
						? transcript.OfType<JsonObject>().Where(item => item["tool_calls"] is JsonArray).SelectMany(item => item["tool_calls"]!.AsArray().OfType<JsonObject>())
						: transcript.OfType<JsonObject>().Where(item => item["type"]?.GetValue<string>() == "function_call");
				var callList = calls.ToList();
				Assert.Equal(count, callList.Count);
				Assert.Equal(results.Select(result => result.Id), callList.Select(call => call[target == TopHatTarget.OpenAIResponses ? "call_id" : "id"]!.GetValue<string>()));
				if (target == TopHatTarget.OpenAIResponses)
				{
					Assert.Equal(count, callList.Select(call => call["id"]!.GetValue<string>()).Distinct(StringComparer.Ordinal).Count());
				}
				var lastArguments = target == TopHatTarget.AnthropicMessages ? callList[^1]["input"]! : JsonNode.Parse((target == TopHatTarget.OpenAIChatCompletions ? callList[^1]["function"]!["arguments"] : callList[^1]["arguments"])!.GetValue<string>())!;
				Assert.Equal(count == 1 ? "known-key" : count == 2 ? "second-key" : "third-key", lastArguments["retrieval_key"]!.GetValue<string>());
				return JsonResponse(ResponseBody(target, Call(target, "reused_id", arguments: new JsonObject { ["retrieval_key"] = count == 1 ? "second-key" : "third-key" })));
			});
			using (var result = await CreateOrchestrator(target, 2).OrchestrateAsync(context, CancellationToken.None))
			{
				Assert.Equal(3, count);
				Assert.Empty(Calls(target, await ReadBodyAsync(result)));
			}
		}
	}

	/// <summary>Unusable Chat envelopes retain their parse-failure metric while recognizable internal calls are removed.</summary>
	[Theory]
	[InlineData("{}", false)]
	[InlineData("{\"choices\":[]}", false)]
	[InlineData("{\"choices\":[{}]}", false)]
	[InlineData("{\"choices\":[{}]}", true)]
	public async Task ChatUnusableFirstChoice_PreservesParseFailureOutcome(string payload, bool includeRecognizableSecondChoice)
	{
		const TopHatTarget target = TopHatTarget.OpenAIChatCompletions;
		var body = JsonNode.Parse(payload)!.AsObject();
		if (includeRecognizableSecondChoice)
		{
			var second = ResponseBody(target, Call(target, "internal_second"))["choices"]![0]!.DeepClone();
			second["index"] = 1;
			body["choices"]!.AsArray().Add(second);
		}
		using (var capture = new MetricsCapture())
		using (var request = Request(target))
		using (var initial = JsonResponse(body))
		{
			var context = Context(request, initial, (_, _) => throw new InvalidOperationException("An unusable first choice cannot create a valid continuation."));
			using (var result = await CreateOrchestrator(target).OrchestrateAsync(context, CancellationToken.None))
			{
				var metric = Assert.Single(capture.ForInstrument("tophat.ccr.orchestrations"));
				Assert.Equal("parse_failure", metric.Tag("outcome"));
				if (includeRecognizableSecondChoice)
				{
					var final = await ReadBodyAsync(result);
					Assert.Null(final["choices"]![1]!["message"]!["tool_calls"]);
					Assert.Equal("stop", final["choices"]![1]!["finish_reason"]!.GetValue<string>());
				}
			}
		}
	}

	/// <summary>Every returned Chat choice is sanitized without selecting or discarding an alternative.</summary>
	[Fact]
	public async Task ChatMultipleChoices_SanitizesAllChoicesWithoutDispatch()
	{
		const TopHatTarget target = TopHatTarget.OpenAIChatCompletions;
		var first = ResponseBody(target);
		var internalOnly = ResponseBody(target, Call(target, "internal_choice_1"))["choices"]![0]!.DeepClone();
		internalOnly["index"] = 1;
		var mixed = ResponseBody(target, Call(target, "internal_choice_2"), Call(target, "foreign_choice", "first_tool"))["choices"]![0]!.DeepClone();
		mixed["index"] = 2;
		first["choices"]!.AsArray().Add(internalOnly);
		first["choices"]!.AsArray().Add(mixed);
		using (var request = Request(target))
		using (var initial = JsonResponse(first))
		{
			var context = Context(request, initial, (_, _) => throw new InvalidOperationException("Independent choices cannot share a follow-up transcript."));
			using (var result = await CreateOrchestrator(target).OrchestrateAsync(context, CancellationToken.None))
			{
				var choices = (await ReadBodyAsync(result))["choices"]!.AsArray();
				Assert.Equal(3, choices.Count);
				Assert.Equal(new[] { 0, 1, 2 }, choices.Select(choice => choice!["index"]!.GetValue<int>()));
				Assert.Equal("stop", choices[0]!["finish_reason"]!.GetValue<string>());
				Assert.Equal("stop", choices[1]!["finish_reason"]!.GetValue<string>());
				Assert.True(choices[1]!["message"]!["tool_calls"] is null || choices[1]!["message"]!["tool_calls"]!.AsArray().Count == 0);
				var remaining = Assert.Single(choices[2]!["message"]!["tool_calls"]!.AsArray());
				Assert.Equal("foreign_choice", remaining!["id"]!.GetValue<string>());
				Assert.Equal("tool_calls", choices[2]!["finish_reason"]!.GetValue<string>());
				Assert.All(choices, choice => Assert.Equal("keep this text", choice!["message"]!["content"]!.GetValue<string>()));
			}
		}
	}

	/// <summary>Responses continuation carries all output items, including reasoning state, in their original order.</summary>
	[Fact]
	public async Task ResponsesFollowUp_PreservesReasoningAndMessageItemsAlongsideCalls()
	{
		const TopHatTarget target = TopHatTarget.OpenAIResponses;
		var body = ResponseBody(target, Call(target, "internal_0"));
		body["output"]!.AsArray().Insert(0, new JsonObject
		{
			["type"] = "reasoning", ["id"] = "rs_private", ["summary"] = new JsonArray(), ["encrypted_content"] = "opaque-provider-state",
		});
		JsonObject? captured = null;
		using (var request = Request(target))
		using (var initial = JsonResponse(body))
		{
			var context = Context(request, initial, async (followUp, _) =>
			{
				captured = JsonNode.Parse(await followUp.Content!.ReadAsStringAsync())!.AsObject();
				return JsonResponse(ResponseBody(target));
			});
			using (await CreateOrchestrator(target).OrchestrateAsync(context, CancellationToken.None))
			{
				Assert.NotNull(captured);
				var input = captured["input"]!.AsArray();
				Assert.Equal(5, input.Count);
				for (var index = 0; index < body["output"]!.AsArray().Count; index++)
				{
					Assert.True(JsonNode.DeepEquals(body["output"]![index], input[index + 1]), input.ToJsonString());
				}
				Assert.Equal("internal_0", input[^1]!["call_id"]!.GetValue<string>());
				Assert.Equal("function_call_output", input[^1]!["type"]!.GetValue<string>());
			}
		}
	}

	/// <summary>Inspected responses/errors stay intact; uninspectable successes cannot pass after private advertisement.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task OwnedTool_InspectedResponsesPassThroughAndUninspectableSuccessesAreRejected(TopHatTarget target)
	{
		var samples = new[]
		{
			(Body: ResponseBody(target).ToJsonString(), Status: HttpStatusCode.OK, ContentType: "application/json", Reject: false),
			(Body: "{\"error\":{\"message\":\"rate limited\"}}", Status: HttpStatusCode.TooManyRequests, ContentType: "application/json", Reject: false),
			(Body: "{ invalid json", Status: HttpStatusCode.OK, ContentType: "application/json", Reject: true),
			(Body: "event: message\ndata: {}\n\n", Status: HttpStatusCode.OK, ContentType: "text/event-stream", Reject: true),
		};
		foreach (var sample in samples)
		{
			using (var request = Request(target))
			using (var initial = new HttpResponseMessage(sample.Status) { Content = new StringContent(sample.Body, Encoding.UTF8, sample.ContentType) })
			{
				var context = Context(request, initial, (_, _) => throw new InvalidOperationException("No retrieval should dispatch."));
				if (sample.Reject)
				{
					await Assert.ThrowsAsync<HttpRequestException>(async () => await CreateOrchestrator(target).OrchestrateAsync(context, CancellationToken.None));
					continue;
				}
				using (var result = await CreateOrchestrator(target).OrchestrateAsync(context, CancellationToken.None))
				{
					Assert.Same(initial, result);
					Assert.Equal(sample.Body, await result.Content.ReadAsStringAsync());
					Assert.Equal(sample.Status, result.StatusCode);
				}
			}
		}
	}

	/// <summary>Cancellation reaches the internal dispatch and is propagated to the caller.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages)]
	[InlineData(TopHatTarget.OpenAIChatCompletions)]
	[InlineData(TopHatTarget.OpenAIResponses)]
	public async Task CancellationDuringFollowUp_IsPropagated(TopHatTarget target)
	{
		using (var cancellation = new CancellationTokenSource())
		using (var request = Request(target))
		using (var initial = JsonResponse(ResponseBody(target, Call(target, "internal_0"))))
		{
			var dispatched = false;
			var context = Context(request, initial, (_, token) =>
			{
				dispatched = true;
				Assert.Equal(cancellation.Token, token);
				cancellation.Cancel();
				return Task.FromCanceled<HttpResponseMessage>(token);
			});
			await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await CreateOrchestrator(target).OrchestrateAsync(context, cancellation.Token));
			Assert.True(dispatched);
		}
	}

	private static ICCROrchestrator CreateOrchestrator(TopHatTarget target, int allowance = 3)
	{
		var options = Options.Create(new CCROptions { MaxIterations = allowance });
		var store = new InMemoryCompressionContextStore(options);
		store.Store("known-key", new JsonNode[] { new JsonObject { ["id"] = 7, ["value"] = "retrieved" } });
		store.Store("second-key", new JsonNode[] { new JsonObject { ["id"] = 42, ["value"] = "new retrieval" } });
		store.Store("third-key", new JsonNode[] { new JsonObject { ["id"] = 99, ["value"] = "must not retrieve after exhaustion" } });
		return target switch
		{
			TopHatTarget.AnthropicMessages => new AnthropicCCROrchestrator(store, options),
			TopHatTarget.OpenAIChatCompletions => new OpenAIChatCompletionsCCROrchestrator(store, options),
			TopHatTarget.OpenAIResponses => new OpenAIResponsesCCROrchestrator(store, options),
			_ => throw new ArgumentOutOfRangeException(nameof(target)),
		};
	}

	private static HttpRequestMessage Request(TopHatTarget target)
	{
		var definition = target switch
		{
			TopHatTarget.AnthropicMessages => CCRToolDefinition.BuildAnthropicToolDefinition(),
			TopHatTarget.OpenAIChatCompletions => CCRToolDefinition.BuildOpenAIChatCompletionsToolDefinition(),
			_ => CCRToolDefinition.BuildOpenAIResponsesToolDefinition(),
		};
		var tools = new JsonArray(definition);
		foreach (var name in new[] { "first_tool", "second_tool" })
		{
			var foreign = definition.DeepClone().AsObject();
			if (target == TopHatTarget.OpenAIChatCompletions)
			{
				foreign["function"]!["name"] = name;
			}
			else
			{
				foreign["name"] = name;
			}
			tools.Add(foreign);
		}
		var body = new JsonObject
		{
			["model"] = "test-model", ["tools"] = tools,
			[target == TopHatTarget.OpenAIResponses ? "input" : "messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Find the missing item." }),
		};
		if (target == TopHatTarget.OpenAIResponses)
		{
			body["store"] = false;
		}
		var endpoint = target switch
		{
			TopHatTarget.AnthropicMessages => "https://api.anthropic.com/v1/messages",
			TopHatTarget.OpenAIChatCompletions => "https://api.openai.com/v1/chat/completions",
			_ => "https://api.openai.com/v1/responses",
		};
		return new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
	}

	private static JsonObject Call(TopHatTarget target, string id, string name = CCRToolDefinition.ToolName, JsonObject? arguments = null)
	{
		arguments ??= new JsonObject { ["retrieval_key"] = "known-key" };
		return target switch
		{
			TopHatTarget.AnthropicMessages => new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = arguments },
			TopHatTarget.OpenAIChatCompletions => new JsonObject { ["type"] = "function", ["id"] = id, ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments.ToJsonString() } },
			_ => new JsonObject { ["type"] = "function_call", ["id"] = "fc_" + id, ["call_id"] = id, ["name"] = name, ["arguments"] = arguments.ToJsonString(), ["status"] = "completed" },
		};
	}

	private static JsonObject ResponseBody(TopHatTarget target, params JsonObject[] calls)
	{
		var body = new JsonObject
		{
			["id"] = "response-id", ["model"] = "test-model",
			["usage"] = new JsonObject { [InputUsageField(target)] = 100, [OutputUsageField(target)] = 20, ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 10 } },
		};
		var callArray = new JsonArray(calls.Select(call => call.DeepClone()).ToArray());
		if (target == TopHatTarget.AnthropicMessages)
		{
			callArray.Insert(0, new JsonObject { ["type"] = "text", ["text"] = "keep this text" });
			body["content"] = callArray;
			body["stop_reason"] = calls.Length == 0 ? "end_turn" : "tool_use";
		}
		else if (target == TopHatTarget.OpenAIChatCompletions)
		{
			var message = new JsonObject { ["role"] = "assistant", ["content"] = "keep this text" };
			if (calls.Length > 0)
			{
				message["tool_calls"] = callArray;
			}
			body["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = message, ["finish_reason"] = calls.Length == 0 ? "stop" : "tool_calls" });
		}
		else
		{
			callArray.Insert(0, new JsonObject { ["type"] = "message", ["role"] = "assistant", ["id"] = "msg_visible", ["status"] = "completed", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = "keep this text", ["annotations"] = new JsonArray() }) });
			body["output"] = callArray;
			body["status"] = "completed";
		}
		return body;
	}

	private static HttpResponseMessage JsonResponse(JsonObject body) => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
	private static CCROrchestrationContext Context(HttpRequestMessage request, HttpResponseMessage initial, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(request, initial, send, "leakage-test", NullLogger.Instance);
	private static async Task<JsonObject> ReadBodyAsync(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
	private static string InputUsageField(TopHatTarget target) => target == TopHatTarget.OpenAIChatCompletions ? "prompt_tokens" : "input_tokens";
	private static string OutputUsageField(TopHatTarget target) => target == TopHatTarget.OpenAIChatCompletions ? "completion_tokens" : "output_tokens";
	private static string? ToolName(TopHatTarget target, JsonObject tool) => (target == TopHatTarget.OpenAIChatCompletions ? tool["function"]?["name"] : tool["name"])?.GetValue<string>();
	private static string? StopReason(TopHatTarget target, JsonObject body) => (target == TopHatTarget.AnthropicMessages ? body["stop_reason"] : target == TopHatTarget.OpenAIChatCompletions ? body["choices"]?[0]?["finish_reason"] : body["status"])?.GetValue<string>();

	private static List<JsonObject> Calls(TopHatTarget target, JsonObject body)
	{
		var items = target switch
		{
			TopHatTarget.AnthropicMessages => body["content"] as JsonArray,
			TopHatTarget.OpenAIChatCompletions => body["choices"]?[0]?["message"]?["tool_calls"] as JsonArray,
			_ => body["output"] as JsonArray,
		};
		return items?.OfType<JsonObject>().Where(item => item["type"]?.GetValue<string>() is "tool_use" or "function" or "function_call").ToList() ?? new List<JsonObject>();
	}

	private static List<(string Id, string Content)> Results(TopHatTarget target, JsonObject request)
	{
		var results = new List<(string Id, string Content)>();
		var items = request[target == TopHatTarget.OpenAIResponses ? "input" : "messages"]!.AsArray();
		foreach (var item in items.OfType<JsonObject>())
		{
			if (target == TopHatTarget.AnthropicMessages && item["role"]?.GetValue<string>() == "user" && item["content"] is JsonArray content)
			{
				foreach (var block in content.OfType<JsonObject>().Where(block => block["type"]?.GetValue<string>() == "tool_result"))
				{
					results.Add((block["tool_use_id"]!.GetValue<string>(), block["content"]!.GetValue<string>()));
				}
			}
			else if (target == TopHatTarget.OpenAIChatCompletions && item["role"]?.GetValue<string>() == "tool")
			{
				results.Add((item["tool_call_id"]!.GetValue<string>(), item["content"]!.GetValue<string>()));
			}
			else if (target == TopHatTarget.OpenAIResponses && item["type"]?.GetValue<string>() == "function_call_output")
			{
				results.Add((item["call_id"]!.GetValue<string>(), item["output"]!.GetValue<string>()));
			}
		}
		return results;
	}

	private static void AssertLastRoundPairing(TopHatTarget target, JsonObject request, string firstId, string secondId)
	{
		var expected = new[] { firstId, secondId };
		var transcript = request[target == TopHatTarget.OpenAIResponses ? "input" : "messages"]!.AsArray();
		if (target == TopHatTarget.AnthropicMessages)
		{
			Assert.Equal("assistant", transcript[^2]!["role"]!.GetValue<string>());
			Assert.Equal(expected, transcript[^2]!["content"]!.AsArray().OfType<JsonObject>().Where(block => block["type"]!.GetValue<string>() == "tool_use").Select(block => block["id"]!.GetValue<string>()));
			Assert.Equal("user", transcript[^1]!["role"]!.GetValue<string>());
			Assert.Equal(expected, transcript[^1]!["content"]!.AsArray().Select(block => block!["tool_use_id"]!.GetValue<string>()));
		}
		else if (target == TopHatTarget.OpenAIChatCompletions)
		{
			Assert.Equal("assistant", transcript[^3]!["role"]!.GetValue<string>());
			Assert.Equal(expected, transcript[^3]!["tool_calls"]!.AsArray().Select(call => call!["id"]!.GetValue<string>()));
			Assert.Equal(expected, transcript.Skip(transcript.Count - 2).Select(item => item!["tool_call_id"]!.GetValue<string>()));
			Assert.All(transcript.Skip(transcript.Count - 2), item => Assert.Equal("tool", item!["role"]!.GetValue<string>()));
		}
		else
		{
			Assert.Equal(expected, transcript.Skip(transcript.Count - 4).Take(2).Select(item => item!["call_id"]!.GetValue<string>()));
			Assert.All(transcript.Skip(transcript.Count - 4).Take(2), item => Assert.Equal("function_call", item!["type"]!.GetValue<string>()));
			Assert.Equal(expected, transcript.Skip(transcript.Count - 2).Select(item => item!["call_id"]!.GetValue<string>()));
			Assert.All(transcript.Skip(transcript.Count - 2), item => Assert.Equal("function_call_output", item!["type"]!.GetValue<string>()));
		}
	}
}
