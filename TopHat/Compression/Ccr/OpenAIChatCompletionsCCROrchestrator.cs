using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TopHat.Diagnostics;
using TopHat.Feedback;
using TopHat.Providers;

namespace TopHat.Compression.CCR;

/// <summary>
/// OpenAI <c>/v1/chat/completions</c> implementation of <see cref="ICCROrchestrator"/>. Inspects
/// the response for a <c>tophat_retrieve</c> tool_call, fulfils it from the
/// <see cref="ICompressionContextStore"/>, appends the assistant tool_calls turn and a
/// <c>role: "tool"</c> message per fulfilment, and re-dispatches upstream until the model emits a
/// final response or the iteration budget is exhausted.
/// </summary>
/// <remarks>
/// <para>
/// <b>Streaming</b>: responses with <c>Content-Type: text/event-stream</c> pass through unchanged
/// — buffering streamed chunks for tool_call detection is deferred to a later phase.
/// </para>
/// <para>
/// <b>Mixed tool_calls</b>: if the response contains a <c>tophat_retrieve</c> tool_call alongside
/// a tool_call for a caller-defined function, the orchestrator passes through unchanged. Chat
/// Completions requires a tool message for every tool_call_id in the assistant turn; we cannot
/// synthesize the caller's tool result, so the caller must handle the unknown
/// <c>tophat_retrieve</c> tool on their side. Documented limitation.
/// </para>
/// </remarks>
public sealed partial class OpenAIChatCompletionsCCROrchestrator : ICCROrchestrator
{
	private readonly ICompressionContextStore _store;
	private readonly IOptions<CCROptions> _options;
	private readonly ICompressionFeedbackStore? _feedbackStore;

	public OpenAIChatCompletionsCCROrchestrator(ICompressionContextStore store, IOptions<CCROptions> options, ICompressionFeedbackStore? feedbackStore = null)
	{
		ArgumentNullException.ThrowIfNull(store);
		ArgumentNullException.ThrowIfNull(options);
		_store = store;
		_options = options;
		_feedbackStore = feedbackStore;
	}

	/// <inheritdoc/>
	public TopHatTarget Target => TopHatTarget.OpenAIChatCompletions;

	/// <inheritdoc/>
	public async ValueTask<HttpResponseMessage> OrchestrateAsync(CCROrchestrationContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);

		var currentResponse = context.InitialResponse;
		var maxIterations = _options.Value.MaxIterations;
		var currentBody = await TryLoadRequestBodyAsync(context.OriginalRequest, cancellationToken).ConfigureAwait(false);

		if (currentBody is null)
		{
			return currentResponse;
		}

		// Per-call upstream count (initial dispatch is hop 1) + cumulative usage across hops so the
		// final response.usage reflects the user's actual billed cost, not just the last hop.
		var hopCount = 1;
		var accumulatedUsage = new JsonObject();

		for (var iteration = 0; iteration < maxIterations; iteration++)
		{
			if (!CanOrchestrate(currentResponse))
			{
				await FinalizeAsync(currentResponse, accumulatedUsage, hopCount, "not_orchestratable", cancellationToken).ConfigureAwait(false);
				return currentResponse;
			}

			await EnsureBufferedAsync(currentResponse, cancellationToken).ConfigureAwait(false);

			var parsedResponse = await TryParseResponseAsync(currentResponse, cancellationToken).ConfigureAwait(false);
			if (parsedResponse is null)
			{
				await FinalizeAsync(currentResponse, accumulatedUsage, hopCount, "parse_failure", cancellationToken).ConfigureAwait(false);
				return currentResponse;
			}

			CCRUsageMerger.Accumulate(accumulatedUsage, parsedResponse["usage"] as JsonObject);

			var assistantMessage = ExtractAssistantMessage(parsedResponse);
			if (assistantMessage is null)
			{
				await FinalizeAsync(currentResponse, accumulatedUsage, hopCount, "parse_failure", cancellationToken).ConfigureAwait(false);
				return currentResponse;
			}

			var toolCalls = ExtractRetrievalToolCalls(assistantMessage, out var hasForeignToolCall);
			if (toolCalls.Count == 0 || hasForeignToolCall)
			{
				var outcome = hasForeignToolCall ? "foreign_tool_use" : (hopCount > 1 ? "multi_hop" : "single_hop");
				await FinalizeAsync(currentResponse, accumulatedUsage, hopCount, outcome, cancellationToken).ConfigureAwait(false);
				return currentResponse;
			}

			// One role:"tool" message per fulfilled call, in order.
			var toolMessages = new List<JsonObject>(toolCalls.Count);
			foreach (var toolCall in toolCalls)
			{
				toolMessages.Add(BuildToolMessage(toolCall));
				this.RecordRetrievalEvent(toolCall, currentBody);
			}

			AppendAssistantTurn(currentBody, assistantMessage);
			AppendToolMessages(currentBody, toolMessages);

			currentResponse.Dispose();

			var followUp = BuildFollowUpRequest(context.OriginalRequest, currentBody);
			currentResponse = await context.SendUpstream(followUp, cancellationToken).ConfigureAwait(false);
			hopCount++;

			LogIteration(context.Logger, context.LocalId, iteration + 1, toolCalls.Count);
		}

		LogBudgetExhausted(context.Logger, context.LocalId, maxIterations);
		await EnsureBufferedAsync(currentResponse, cancellationToken).ConfigureAwait(false);
		var finalParsed = await TryParseResponseAsync(currentResponse, cancellationToken).ConfigureAwait(false);
		if (finalParsed is not null)
		{
			CCRUsageMerger.Accumulate(accumulatedUsage, finalParsed["usage"] as JsonObject);

			// Record budget-exhausted retrievals for any tool_calls in the final response
			// — these are retrievals the model wanted but couldn't get within budget.
			var assistantMessage = ExtractAssistantMessage(finalParsed);
			if (assistantMessage is not null)
			{
				var unfulfilled = ExtractRetrievalToolCalls(assistantMessage, out _);
				foreach (var toolCall in unfulfilled)
				{
					this.RecordRetrievalEvent(toolCall, currentBody, forceBudgetExhausted: true);
				}
			}
		}
		await FinalizeAsync(currentResponse, accumulatedUsage, hopCount, "budget_exhausted", cancellationToken).ConfigureAwait(false);
		return currentResponse;
	}

	/// <summary>
	/// Records a retrieval event for the feedback store. Tool name is resolved from the
	/// retrieval_key in the tool_call's parsed arguments. Best-effort — silently drops if
	/// store unregistered or tool name unresolvable.
	/// </summary>
	private void RecordRetrievalEvent(JsonObject toolCall, JsonObject requestBody, bool forceBudgetExhausted = false)
	{
		if (this._feedbackStore is null)
		{
			return;
		}

		var argumentsString = (toolCall["function"] as JsonObject)?["arguments"]?.GetValue<string>();
		if (string.IsNullOrEmpty(argumentsString))
		{
			return;
		}

		JsonObject? input;
		try
		{
			input = JsonNode.Parse(argumentsString) as JsonObject;
		}
		catch (JsonException)
		{
			return;
		}

		var retrievalKey = input?[CCRToolDefinition.RetrievalKeyField]?.GetValue<string>();
		if (string.IsNullOrEmpty(retrievalKey))
		{
			return;
		}

		var toolName = ToolNameResolver.ResolveByRetrievalKey(requestBody, this.Target, retrievalKey);
		if (string.IsNullOrEmpty(toolName))
		{
			return;
		}

		RetrievalKind kind;
		if (forceBudgetExhausted)
		{
			kind = RetrievalKind.BudgetExhausted;
		}
		else
		{
			var hasIdsFilter = input?["ids"] is JsonArray idArray && idArray.Count > 0;
			kind = hasIdsFilter ? RetrievalKind.Search : RetrievalKind.Full;
		}

		this._feedbackStore.RecordRetrieval(toolName, kind);
	}

	private ValueTask FinalizeAsync(HttpResponseMessage response, JsonObject accumulatedUsage, int hopCount, string outcome, CancellationToken cancellationToken)
	{
		var targetTag = this.Target.ToString();
		TopHatMetrics.CCROrchestrations.Add(1, new TagList { { "target", targetTag }, { "outcome", outcome } });
		TopHatMetrics.CCRHops.Record(hopCount, new TagList { { "target", targetTag } });

		if (hopCount <= 1)
		{
			return ValueTask.CompletedTask;
		}

		return CCRUsageMerger.ApplyAsync(response, accumulatedUsage, hopCount, cancellationToken);
	}

	private static async ValueTask EnsureBufferedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		if (response.Content is ByteArrayContent)
		{
			return;
		}

		var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
		var originalHeaders = response.Content.Headers;
		var replacement = new ByteArrayContent(bytes);

		foreach (var header in originalHeaders)
		{
			replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
		}

		response.Content.Dispose();
		response.Content = replacement;
	}

	private static bool CanOrchestrate(HttpResponseMessage response)
	{
		if (!response.IsSuccessStatusCode)
		{
			return false;
		}

		var contentType = response.Content.Headers.ContentType?.MediaType;
		return string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase);
	}

	private static async ValueTask<JsonObject?> TryParseResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		try
		{
			var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
			return JsonNode.Parse(bytes) as JsonObject;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static async ValueTask<JsonObject?> TryLoadRequestBodyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		if (request.Content is null)
		{
			return null;
		}

		try
		{
			var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
			return JsonNode.Parse(bytes) as JsonObject;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	/// <summary>
	/// Reaches into <c>choices[0].message</c>. CCR only inspects the first choice — if the caller
	/// requested <c>n &gt; 1</c> they're in custom-orchestration territory and we pass through.
	/// </summary>
	private static JsonObject? ExtractAssistantMessage(JsonObject response)
	{
		if (response["choices"] is not JsonArray choices || choices.Count == 0)
		{
			return null;
		}

		if (choices[0] is not JsonObject firstChoice)
		{
			return null;
		}

		return firstChoice["message"] as JsonObject;
	}

	private static List<JsonObject> ExtractRetrievalToolCalls(JsonObject assistantMessage, out bool hasForeignToolCall)
	{
		hasForeignToolCall = false;
		var matches = new List<JsonObject>();

		if (assistantMessage["tool_calls"] is not JsonArray toolCalls)
		{
			return matches;
		}

		foreach (var call in toolCalls)
		{
			if (call is not JsonObject obj)
			{
				continue;
			}

			var function = obj["function"] as JsonObject;
			var name = function?["name"]?.GetValue<string>();

			if (string.Equals(name, CCRToolDefinition.ToolName, StringComparison.Ordinal))
			{
				matches.Add(obj);
			}
			else
			{
				hasForeignToolCall = true;
			}
		}

		return matches;
	}

	private JsonObject BuildToolMessage(JsonObject toolCall)
	{
		var toolCallId = toolCall["id"]?.GetValue<string>() ?? string.Empty;
		var function = toolCall["function"] as JsonObject;
		var argumentsString = function?["arguments"]?.GetValue<string>();
		var input = TryParseArguments(argumentsString);
		var retrievalKey = input?[CCRToolDefinition.RetrievalKeyField]?.GetValue<string>();

		var items = new List<JsonNode>();

		if (!string.IsNullOrEmpty(retrievalKey))
		{
			var ids = ExtractIdFilter(input);
			var limit = ExtractLimit(input);
			var retrieved = _store.Retrieve(retrievalKey, ids, limit);
			items.AddRange(retrieved);
		}

		var resultArray = new JsonArray();
		foreach (var item in items)
		{
			resultArray.Add(item);
		}

		return new JsonObject
		{
			["role"] = "tool",
			["tool_call_id"] = toolCallId,
			["content"] = resultArray.ToJsonString(),
		};
	}

	/// <summary>
	/// Chat Completions encodes tool_call arguments as a JSON-encoded string. Parsing failure is
	/// silent because the model can emit malformed JSON; downstream code treats a null input as
	/// "no retrieval key" and the orchestrator returns an empty array.
	/// </summary>
	private static JsonObject? TryParseArguments(string? argumentsString)
	{
		if (string.IsNullOrEmpty(argumentsString))
		{
			return null;
		}

		try
		{
			return JsonNode.Parse(argumentsString) as JsonObject;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static HashSet<int>? ExtractIdFilter(JsonObject? input)
	{
		if (input is null || input["ids"] is not JsonArray idArray)
		{
			return null;
		}

		var ids = new HashSet<int>();

		foreach (var node in idArray)
		{
			if (node is null)
			{
				continue;
			}

			try
			{
				ids.Add(node.GetValue<int>());
			}
			catch (FormatException)
			{
			}
			catch (InvalidOperationException)
			{
			}
		}

		return ids.Count == 0 ? null : ids;
	}

	private int ExtractLimit(JsonObject? input)
	{
		var ceiling = _options.Value.RetrievalItemCeiling;

		if (input is null || input["limit"] is null)
		{
			return Math.Min(10, ceiling);
		}

		try
		{
			var requested = input["limit"]!.GetValue<int>();
			return Math.Clamp(requested, 1, ceiling);
		}
		catch (FormatException)
		{
			return Math.Min(10, ceiling);
		}
		catch (InvalidOperationException)
		{
			return Math.Min(10, ceiling);
		}
	}

	private static void AppendAssistantTurn(JsonObject requestBody, JsonObject assistantMessage)
	{
		if (requestBody["messages"] is not JsonArray messages)
		{
			return;
		}

		// Echo the assistant message verbatim — Chat Completions requires the original tool_calls
		// array (with ids) so the follow-up tool messages have something to bind tool_call_id to.
		messages.Add(assistantMessage.DeepClone());
	}

	private static void AppendToolMessages(JsonObject requestBody, List<JsonObject> toolMessages)
	{
		if (requestBody["messages"] is not JsonArray messages)
		{
			return;
		}

		foreach (var msg in toolMessages)
		{
			messages.Add(msg);
		}
	}

	private static HttpRequestMessage BuildFollowUpRequest(HttpRequestMessage original, JsonObject body)
	{
		var followUp = new HttpRequestMessage(original.Method, original.RequestUri)
		{
			Version = original.Version,
			VersionPolicy = original.VersionPolicy,
			Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
		};

		foreach (var header in original.Headers)
		{
			followUp.Headers.TryAddWithoutValidation(header.Key, header.Value);
		}

		if (original.Content is not null)
		{
			foreach (var header in original.Content.Headers)
			{
				if (header.Key == "Content-Type" || header.Key == "Content-Length")
				{
					continue;
				}
				followUp.Content!.Headers.TryAddWithoutValidation(header.Key, header.Value);
			}
		}

		return followUp;
	}

	[LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "CCR (OpenAI Chat) fulfilled {ToolCallCount} tophat_retrieve tool_call(s) on iteration {Iteration} (localId={LocalId}).")]
	private static partial void LogIteration(ILogger logger, string localId, int iteration, int toolCallCount);

	[LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "CCR (OpenAI Chat) hit iteration budget {MaxIterations}; returning last response as-is (localId={LocalId}).")]
	private static partial void LogBudgetExhausted(ILogger logger, string localId, int maxIterations);
}
