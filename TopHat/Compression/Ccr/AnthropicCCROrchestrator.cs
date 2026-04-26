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
/// Anthropic <c>/v1/messages</c> implementation of <see cref="ICCROrchestrator"/>. Inspects the
/// response for a <c>tophat_retrieve</c> tool_use, fulfils it from the
/// <see cref="ICompressionContextStore"/>, appends the tool_use + tool_result to the
/// conversation, and re-dispatches upstream until the model emits a final response or the
/// iteration budget is exhausted.
/// </summary>
/// <remarks>
/// <para>
/// <b>Streaming</b>: responses with <c>Content-Type: text/event-stream</c> pass through
/// unchanged. Streaming CCR requires buffering the response to detect tool_use — deferred to a
/// later phase. See <see cref="CanOrchestrate"/> for the full list of short-circuits.
/// </para>
/// <para>
/// <b>Mixed tool_uses</b>: if the response contains a <c>tophat_retrieve</c> tool_use alongside
/// a tool_use for a caller-defined tool, the orchestrator passes through unchanged — the
/// Anthropic protocol requires a <c>tool_result</c> for every emitted <c>tool_use</c> in a
/// single user message, and we can't synthesize the caller's tool result. The caller will need
/// to handle the unknown <c>tophat_retrieve</c> tool on their side; this is a documented limit.
/// </para>
/// </remarks>
public sealed partial class AnthropicCCROrchestrator : ICCROrchestrator
{
	private readonly ICompressionContextStore _store;
	private readonly IOptions<CCROptions> _options;
	private readonly ICompressionFeedbackStore? _feedbackStore;

	public AnthropicCCROrchestrator(ICompressionContextStore store, IOptions<CCROptions> options, ICompressionFeedbackStore? feedbackStore = null)
	{
		ArgumentNullException.ThrowIfNull(store);
		ArgumentNullException.ThrowIfNull(options);
		_store = store;
		_options = options;
		_feedbackStore = feedbackStore;
	}

	/// <inheritdoc/>
	public TopHatTarget Target => TopHatTarget.AnthropicMessages;

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

			// Buffer before parsing so the caller can still read the final response body. If the
			// response was already buffered by the pipeline (default HttpClient behavior), this is
			// a cheap no-op.
			await EnsureBufferedAsync(currentResponse, cancellationToken).ConfigureAwait(false);

			var parsedResponse = await TryParseResponseAsync(currentResponse, cancellationToken).ConfigureAwait(false);
			if (parsedResponse is null)
			{
				await FinalizeAsync(currentResponse, accumulatedUsage, hopCount, "parse_failure", cancellationToken).ConfigureAwait(false);
				return currentResponse;
			}

			CCRUsageMerger.Accumulate(accumulatedUsage, parsedResponse["usage"] as JsonObject);

			var toolUses = ExtractRetrievalToolUses(parsedResponse, out var hasForeignToolUse);
			if (toolUses.Count == 0 || hasForeignToolUse)
			{
				// "single_hop" when the model just answered (no retrieval at all on iteration 0);
				// "multi_hop" when CCR fired and the model has now terminated; "foreign_tool_use"
				// when retrieval was mixed with a caller-defined tool we can't fulfil.
				var outcome = hasForeignToolUse ? "foreign_tool_use" : (hopCount > 1 ? "multi_hop" : "single_hop");
				await FinalizeAsync(currentResponse, accumulatedUsage, hopCount, outcome, cancellationToken).ConfigureAwait(false);
				return currentResponse;
			}

			// Fulfil each retrieval call from the store.
			var toolResults = new List<JsonObject>(toolUses.Count);
			foreach (var toolUse in toolUses)
			{
				toolResults.Add(BuildToolResultBlock(toolUse));
				this.RecordRetrievalEvent(toolUse, currentBody);
			}

			// Extend the conversation: assistant message (echoing the original response's content)
			// + user message carrying every tool_result in order.
			AppendAssistantTurn(currentBody, parsedResponse);
			AppendToolResultTurn(currentBody, toolResults);

			// Dispose the spent response — we're replacing it with the follow-up's.
			currentResponse.Dispose();

			// Fire the follow-up upstream. The delegate is bound to base.SendAsync on the handler
			// so this traverses any remaining inner handlers without re-entering TopHatHandler.
			var followUp = BuildFollowUpRequest(context.OriginalRequest, currentBody);
			currentResponse = await context.SendUpstream(followUp, cancellationToken).ConfigureAwait(false);
			hopCount++;

			LogIteration(context.Logger, context.LocalId, iteration + 1, toolUses.Count);
		}

		// Budget exhausted. Buffer + accumulate the final hop's usage so the rewrite reflects
		// every hop, including the one that came back over budget.
		LogBudgetExhausted(context.Logger, context.LocalId, maxIterations);
		await EnsureBufferedAsync(currentResponse, cancellationToken).ConfigureAwait(false);
		var finalParsed = await TryParseResponseAsync(currentResponse, cancellationToken).ConfigureAwait(false);
		if (finalParsed is not null)
		{
			CCRUsageMerger.Accumulate(accumulatedUsage, finalParsed["usage"] as JsonObject);

			// Any tool_uses in the budget-exhausted response are retrievals the model wanted but
			// couldn't get. Record them as BudgetExhausted — strongest "this tool needed everything"
			// signal in the feedback store.
			var unfulfilledToolUses = ExtractRetrievalToolUses(finalParsed, out _);
			foreach (var toolUse in unfulfilledToolUses)
			{
				this.RecordRetrievalEvent(toolUse, currentBody, forceBudgetExhausted: true);
			}
		}
		await FinalizeAsync(currentResponse, accumulatedUsage, hopCount, "budget_exhausted", cancellationToken).ConfigureAwait(false);
		return currentResponse;
	}

	/// <summary>
	/// Records a retrieval event in the feedback store keyed by the tool name. Resolves tool
	/// name from the retrieval_key by walking the request body. Best-effort — if the
	/// feedback store is unregistered or the tool name can't be resolved, the event is
	/// dropped silently rather than producing partial/incorrect data.
	/// </summary>
	private void RecordRetrievalEvent(JsonObject toolUse, JsonObject requestBody, bool forceBudgetExhausted = false)
	{
		if (this._feedbackStore is null)
		{
			return;
		}

		var input = toolUse["input"] as JsonObject;
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
			// Search if the model targeted specific ids; Full otherwise (give-me-everything).
			var hasIdsFilter = input?["ids"] is JsonArray idArray && idArray.Count > 0;
			kind = hasIdsFilter ? RetrievalKind.Search : RetrievalKind.Full;
		}

		this._feedbackStore.RecordRetrieval(toolName, kind);
	}

	/// <summary>
	/// Emits CCR metrics for the orchestration outcome and stamps cumulative usage + hop header
	/// on the final response when CCR drove more than one upstream call. Single-hop case skips
	/// the body rewrite (response is exactly what upstream returned) but still records metrics
	/// so the no-op path is visible in dashboards.
	/// </summary>
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
		if (!string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		return true;
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
	/// Returns every <c>tool_use</c> block whose name matches <see cref="CCRToolDefinition.ToolName"/>.
	/// Sets <paramref name="hasForeignToolUse"/> when a non-retrieval tool_use is also present —
	/// signals the orchestrator to pass through so the caller's tool-loop handles the mix.
	/// </summary>
	private static List<JsonObject> ExtractRetrievalToolUses(JsonObject response, out bool hasForeignToolUse)
	{
		hasForeignToolUse = false;
		var matches = new List<JsonObject>();

		if (response["content"] is not JsonArray content)
		{
			return matches;
		}

		foreach (var block in content)
		{
			if (block is not JsonObject obj)
			{
				continue;
			}

			if (obj["type"]?.GetValue<string>() != "tool_use")
			{
				continue;
			}

			var name = obj["name"]?.GetValue<string>();
			if (string.Equals(name, CCRToolDefinition.ToolName, StringComparison.Ordinal))
			{
				matches.Add(obj);
			}
			else
			{
				hasForeignToolUse = true;
			}
		}

		return matches;
	}

	private JsonObject BuildToolResultBlock(JsonObject toolUse)
	{
		var toolUseId = toolUse["id"]?.GetValue<string>() ?? string.Empty;
		var input = toolUse["input"] as JsonObject;
		var retrievalKey = input?[CCRToolDefinition.RetrievalKeyField]?.GetValue<string>();

		var items = new List<JsonNode>();

		if (!string.IsNullOrEmpty(retrievalKey))
		{
			var ids = ExtractIdFilter(input);
			var limit = ExtractLimit(input);
			var retrieved = _store.Retrieve(retrievalKey, ids, limit);
			items.AddRange(retrieved);
		}

		// Wrap results in a JSON array string — Anthropic tool_result content accepts a string,
		// and the raw JSON array format is what the model will parse naturally.
		var resultArray = new JsonArray();
		foreach (var item in items)
		{
			resultArray.Add(item);
		}

		return new JsonObject
		{
			["type"] = "tool_result",
			["tool_use_id"] = toolUseId,
			["content"] = resultArray.ToJsonString(),
		};
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
				// Silently skip malformed entries; model may have emitted a string-typed id.
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

	private static void AppendAssistantTurn(JsonObject requestBody, JsonObject response)
	{
		if (requestBody["messages"] is not JsonArray messages)
		{
			return;
		}

		var content = response["content"]?.DeepClone() ?? new JsonArray();

		var assistantMessage = new JsonObject
		{
			["role"] = "assistant",
			["content"] = content,
		};

		messages.Add(assistantMessage);
	}

	private static void AppendToolResultTurn(JsonObject requestBody, List<JsonObject> toolResults)
	{
		if (requestBody["messages"] is not JsonArray messages)
		{
			return;
		}

		var content = new JsonArray();
		foreach (var block in toolResults)
		{
			content.Add(block);
		}

		var userMessage = new JsonObject
		{
			["role"] = "user",
			["content"] = content,
		};

		messages.Add(userMessage);
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
			// Carry through content headers that aren't already set (e.g., Anthropic-Version on content?
			// usually it's on the request headers, but preserve whatever was on content too).
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

	[LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "CCR orchestrator fulfilled {ToolUseCount} tophat_retrieve tool_use(s) on iteration {Iteration} (localId={LocalId}).")]
	private static partial void LogIteration(ILogger logger, string localId, int iteration, int toolUseCount);

	[LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "CCR orchestrator hit iteration budget {MaxIterations}; returning last response as-is (localId={LocalId}).")]
	private static partial void LogBudgetExhausted(ILogger logger, string localId, int maxIterations);
}
