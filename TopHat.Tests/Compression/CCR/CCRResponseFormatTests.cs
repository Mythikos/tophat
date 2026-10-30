using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TopHat.Compression.CCR.DependencyInjection;
using TopHat.DependencyInjection;
using TopHat.Providers;
using TopHat.Relevance.BM25.DependencyInjection;
using TopHat.Tests.Support;
using Xunit;

namespace TopHat.Tests.Compression.CCR;

/// <summary>Checks the whole response boundary after proven synthetic tool injection.</summary>
public sealed class CCRResponseFormatTests
{
	/// <summary>Provider response formatting must not expose a tool TopHat injected into a nonstreaming request.</summary>
	[Theory]
	[InlineData(TopHatTarget.AnthropicMessages, "json")]
	[InlineData(TopHatTarget.OpenAIChatCompletions, "json")]
	[InlineData(TopHatTarget.OpenAIResponses, "json")]
	[InlineData(TopHatTarget.AnthropicMessages, "suffix")]
	[InlineData(TopHatTarget.OpenAIChatCompletions, "suffix")]
	[InlineData(TopHatTarget.OpenAIResponses, "suffix")]
	[InlineData(TopHatTarget.AnthropicMessages, "missing")]
	[InlineData(TopHatTarget.OpenAIChatCompletions, "missing")]
	[InlineData(TopHatTarget.OpenAIResponses, "missing")]
	[InlineData(TopHatTarget.AnthropicMessages, "sse")]
	[InlineData(TopHatTarget.OpenAIChatCompletions, "sse")]
	[InlineData(TopHatTarget.OpenAIResponses, "sse")]
	[InlineData(TopHatTarget.AnthropicMessages, "malformed")]
	[InlineData(TopHatTarget.OpenAIChatCompletions, "malformed")]
	[InlineData(TopHatTarget.OpenAIResponses, "malformed")]
	[InlineData(TopHatTarget.OpenAIChatCompletions, "legacy")]
	public async Task InjectedCall_NeverReturnsToCallerAcrossResponseFormats(TopHatTarget target, string format)
	{
		var dispatches = 0;
		var (client, _, services) = TransformHandlerFactory.Build(collection =>
		{
			collection.AddTopHatCCR();
			collection.AddTopHatBm25Relevance();
			collection.AddTopHatJsonContextCompressor(options =>
			{
				options.MinTokensToCrush = 1;
				options.UnfrozenMessageCount = int.MaxValue;
			});
		}, behavior: async (request, cancellationToken) =>
		{
			dispatches++;
			Assert.True(dispatches <= 2);
			if (dispatches == 2)
			{
				return Response(Terminal(target).ToJsonString(), "application/json");
			}
			var actual = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
			Assert.Contains(actual["tools"]!.AsArray().OfType<JsonObject>(), tool => (target == TopHatTarget.OpenAIChatCompletions ? tool["function"] : tool)?["name"]?.GetValue<string>() == "tophat_retrieve");
			var compressed = target switch
			{
				TopHatTarget.AnthropicMessages => actual["messages"]![2]!["content"]![0]!["content"]!.GetValue<string>(),
				TopHatTarget.OpenAIChatCompletions => actual["messages"]![2]!["content"]!.GetValue<string>(),
				_ => actual["input"]![2]!["output"]!.GetValue<string>(),
			};
			var metadata = Assert.Single(JsonNode.Parse(compressed)!.AsArray().OfType<JsonObject>(), item => item.ContainsKey("_tophat_compression"));
			var key = metadata["_tophat_compression"]!["retrieval_key"]!.GetValue<string>();
			var callBody = InternalCall(target, key, format == "legacy").ToJsonString();
			return format switch
			{
				"suffix" => Response(callBody, "application/vnd.provider+json"),
				"missing" => Response(callBody, null),
				"sse" => Response(StreamedCall(target, key), "text/event-stream"),
				"malformed" => Response(callBody[..^1], "application/json"),
				_ => Response(callBody, "application/json"),
			};
		});
		using ((IDisposable)services)
		using (client)
		using (var request = new HttpRequestMessage(HttpMethod.Post, Url(target)) { Content = new StringContent(RequestBody(target).ToJsonString(), Encoding.UTF8, "application/json") })
		{
			try
			{
				using (var result = await client.SendAsync(request))
				{
					var returned = await result.Content.ReadAsStringAsync();
					Assert.DoesNotContain("tophat_retrieve", returned, StringComparison.Ordinal);
					if (format is "json" or "suffix")
					{
						Assert.Equal(2, dispatches);
						Assert.Contains("Done.", returned, StringComparison.Ordinal);
					}
				}
			}
			catch (HttpRequestException) when (format is "missing" or "sse" or "malformed" or "legacy")
			{
				// Rejecting an unsupported response is safe; returning its private call is not.
				Assert.Equal(1, dispatches);
			}
		}
	}

	private static string StreamedCall(TopHatTarget target, string key)
	{
		var response = InternalCall(target, key, legacy: false);
		if (target == TopHatTarget.AnthropicMessages)
		{
			var start = new JsonObject { ["type"] = "content_block_start", ["index"] = 0, ["content_block"] = response["content"]![0]!.DeepClone() };
			return "event: content_block_start\ndata: " + start.ToJsonString() + "\n\n";
		}
		if (target == TopHatTarget.OpenAIChatCompletions)
		{
			var call = response["choices"]![0]!["message"]!["tool_calls"]![0]!.DeepClone();
			call["index"] = 0;
			var chunk = new JsonObject
			{
				["id"] = "chatcmpl_audit", ["object"] = "chat.completion.chunk",
				["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = new JsonObject { ["role"] = "assistant", ["tool_calls"] = new JsonArray(call) }, ["finish_reason"] = null }),
			};
			return "data: " + chunk.ToJsonString() + "\n\ndata: [DONE]\n\n";
		}
		var item = new JsonObject { ["type"] = "response.output_item.added", ["sequence_number"] = 0, ["output_index"] = 0, ["item"] = response["output"]![0]!.DeepClone() };
		return "event: response.output_item.added\ndata: " + item.ToJsonString() + "\n\n";
	}

	private static HttpResponseMessage Response(string body, string? mediaType)
	{
		var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
		response.Content.Headers.ContentType = mediaType is null ? null : new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
		return response;
	}

	private static string Url(TopHatTarget target) => target switch
	{
		TopHatTarget.AnthropicMessages => "https://api.anthropic.com/v1/messages",
		TopHatTarget.OpenAIChatCompletions => "https://api.openai.com/v1/chat/completions",
		_ => "https://api.openai.com/v1/responses",
	};

	private static JsonObject RequestBody(TopHatTarget target)
	{
		var payload = JsonSerializer.Serialize(Enumerable.Range(1, 100).Select(index => new { id = index, status = "processed", message = "record inserted successfully into database" }));
		var body = new JsonObject { ["model"] = "test-model" };
		if (target == TopHatTarget.AnthropicMessages)
		{
			body["max_tokens"] = 512;
			body["tools"] = new JsonArray(new JsonObject { ["name"] = "get_logs", ["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() } });
			body["messages"] = new JsonArray
			{
				new JsonObject { ["role"] = "user", ["content"] = "summarize logs" },
				new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = "logs", ["name"] = "get_logs", ["input"] = new JsonObject() }) },
				new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = "logs", ["content"] = payload }) },
			};
		}
		else if (target == TopHatTarget.OpenAIChatCompletions)
		{
			body["tools"] = new JsonArray(new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = "get_logs", ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() } } });
			body["messages"] = new JsonArray
			{
				new JsonObject { ["role"] = "user", ["content"] = "summarize logs" },
				new JsonObject { ["role"] = "assistant", ["tool_calls"] = new JsonArray(new JsonObject { ["type"] = "function", ["id"] = "logs", ["function"] = new JsonObject { ["name"] = "get_logs", ["arguments"] = "{}" } }) },
				new JsonObject { ["role"] = "tool", ["tool_call_id"] = "logs", ["content"] = payload },
			};
		}
		else
		{
			body["store"] = false;
			body["tools"] = new JsonArray(new JsonObject { ["type"] = "function", ["name"] = "get_logs", ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() } });
			body["input"] = new JsonArray
			{
				new JsonObject { ["role"] = "user", ["content"] = "summarize logs" },
				new JsonObject { ["type"] = "function_call", ["id"] = "fc_logs", ["call_id"] = "logs", ["name"] = "get_logs", ["arguments"] = "{}" },
				new JsonObject { ["type"] = "function_call_output", ["call_id"] = "logs", ["output"] = payload },
			};
		}
		return body;
	}

	private static JsonObject InternalCall(TopHatTarget target, string key, bool legacy)
	{
		var arguments = new JsonObject { ["retrieval_key"] = key };
		if (target == TopHatTarget.AnthropicMessages)
		{
			return new JsonObject { ["role"] = "assistant", ["stop_reason"] = "tool_use", ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = "internal", ["name"] = "tophat_retrieve", ["input"] = arguments }) };
		}
		if (target == TopHatTarget.OpenAIChatCompletions)
		{
			var function = new JsonObject { ["name"] = "tophat_retrieve", ["arguments"] = arguments.ToJsonString() };
			var message = new JsonObject { ["role"] = "assistant", ["content"] = null };
			if (legacy)
			{
				message["function_call"] = function;
			}
			else
			{
				message["tool_calls"] = new JsonArray(new JsonObject { ["type"] = "function", ["id"] = "internal", ["function"] = function });
			}
			return new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["finish_reason"] = legacy ? "function_call" : "tool_calls", ["message"] = message }) };
		}
		return new JsonObject { ["status"] = "completed", ["output"] = new JsonArray(new JsonObject { ["type"] = "function_call", ["id"] = "fc_internal", ["call_id"] = "internal", ["name"] = "tophat_retrieve", ["arguments"] = arguments.ToJsonString() }) };
	}

	private static JsonObject Terminal(TopHatTarget target) => target switch
	{
		TopHatTarget.AnthropicMessages => new JsonObject { ["role"] = "assistant", ["stop_reason"] = "end_turn", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Done." }) },
		TopHatTarget.OpenAIChatCompletions => new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["finish_reason"] = "stop", ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = "Done." } }) },
		_ => new JsonObject { ["status"] = "completed", ["output"] = new JsonArray(new JsonObject { ["type"] = "message", ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = "Done." }) }) },
	};
}
