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
/// OpenAI <c>/v1/responses</c> implementation of <see cref="ICCROrchestrator"/>. Inspects the
/// response's <c>output</c> array for a <c>function_call</c> targeting <c>tophat_retrieve</c>,
/// fulfils it from the <see cref="ICompressionContextStore"/>, appends the function_call item
/// and a matching <c>function_call_output</c> to the request's <c>input</c> array, and
/// re-dispatches upstream until the model emits a final response or the iteration budget is
/// exhausted.
/// </summary>
/// <remarks>
/// <para>
/// <b>Streaming</b>: responses with <c>Content-Type: text/event-stream</c> pass through unchanged.
/// </para>
/// <para>
/// <b>Mixed function_calls</b>: if the response contains a <c>tophat_retrieve</c> function_call
/// alongside a function_call for a caller-defined tool, the orchestrator passes through unchanged
/// — Responses requires a function_call_output for every emitted function_call, and we cannot
/// synthesize the caller's output. Documented limitation.
/// </para>
/// <para>
/// <b>String-form input</b>: when <c>input</c> is a plain string (no array), there is nowhere to
/// splice the follow-up turn, so the orchestrator passes through. Such requests can't have
/// produced compressed tool_results in the first place — this is a defensive guard.
/// </para>
/// </remarks>
public sealed partial class OpenAIResponsesCCROrchestrator : ICCROrchestrator
{
	private readonly ICompressionContextStore _store;
	private readonly IOptions<CCROptions> _options;
	private readonly ICompressionFeedbackStore? _feedbackStore;

	public OpenAIResponsesCCROrchestrator(ICompressionContextStore store, IOptions<CCROptions> options, ICompressionFeedbackStore? feedbackStore = null)
	{
		ArgumentNullException.ThrowIfNull(store);
		ArgumentNullException.ThrowIfNull(options);
		_store = store;
		_options = options;
		_feedbackStore = feedbackStore;
	}

	/// <inheritdoc/>
	public TopHatTarget Target => TopHatTarget.OpenAIResponses;

	/// <inheritdoc/>
	public async ValueTask<HttpResponseMessage> OrchestrateAsync(CCROrchestrationContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);

		var currentResponse = context.InitialResponse;
		var maxIterations = _options.Value.MaxIterations;
		var currentBody = await TryLoadRequestBodyAsync(context.OriginalRequest, cancellationToken).ConfigureAwait(false);

		// Responses can ship `input` as a plain string. CCR only operates when `input` is an
		// array — there's no way to splice a follow-up turn into a string-shaped input.
		if (currentBody is null || currentBody["input"] is not JsonArray)
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

			var functionCalls = ExtractRetrievalFunctionCalls(parsedResponse, out var hasForeignFunctionCall);
			if (functionCalls.Count == 0 || hasForeignFunctionCall)
			{
				var outcome = hasForeignFunctionCall ? "foreign_tool_use" : (hopCount > 1 ? "multi_hop" : "single_hop");
				await FinalizeAsync(currentResponse, accumulatedUsage, hopCount, outcome, cancellationToken).ConfigureAwait(false);
				return currentResponse;
			}

			// One function_call_output per fulfilled call — paired by call_id.
			var outputs = new List<JsonObject>(functionCalls.Count);
			foreach (var call in functionCalls)
			{
				outputs.Add(BuildFunctionCallOutput(call));
				this.RecordRetrievalEvent(call, currentBody);
			}

			AppendFunctionCallTurn(currentBody, functionCalls);
			AppendFunctionCallOutputs(currentBody, outputs);

			currentResponse.Dispose();

			var followUp = BuildFollowUpRequest(context.OriginalRequest, currentBody);
			currentResponse = await context.SendUpstream(followUp, cancellationToken).ConfigureAwait(false);
			hopCount++;

			LogIteration(context.Logger, context.LocalId, iteration + 1, functionCalls.Count);
		}

		LogBudgetExhausted(context.Logger, context.LocalId, maxIterations);
		await EnsureBufferedAsync(currentResponse, cancellationToken).ConfigureAwait(false);
		var finalParsed = await TryParseResponseAsync(currentResponse, cancellationToken).ConfigureAwait(false);
		if (finalParsed is not null)
		{
			CCRUsageMerger.Accumulate(accumulatedUsage, finalParsed["usage"] as JsonObject);

			// Record budget-exhausted retrievals for any unfulfilled function_calls.
			var unfulfilled = ExtractRetrievalFunctionCalls(finalParsed, out _);
			foreach (var call in unfulfilled)
			{
				this.RecordRetrievalEvent(call, currentBody, forceBudgetExhausted: true);
			}
		}
		await FinalizeAsync(currentResponse, accumulatedUsage, hopCount, "budget_exhausted", cancellationToken).ConfigureAwait(false);
		return currentResponse;
	}

	/// <summary>
	/// Records a retrieval event for the feedback store. Tool name resolved from the
	/// retrieval_key in the function_call's parsed arguments. Best-effort.
	/// </summary>
	private void RecordRetrievalEvent(JsonObject functionCall, JsonObject requestBody, bool forceBudgetExhausted = false)
	{
		if (this._feedbackStore is null)
		{
			return;
		}

		var argumentsString = functionCall["arguments"]?.GetValue<string>();
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

	private static List<JsonObject> ExtractRetrievalFunctionCalls(JsonObject response, out bool hasForeignFunctionCall)
	{
		hasForeignFunctionCall = false;
		var matches = new List<JsonObject>();

		if (response["output"] is not JsonArray output)
		{
			return matches;
		}

		foreach (var item in output)
		{
			if (item is not JsonObject obj)
			{
				continue;
			}

			if (obj["type"]?.GetValue<string>() != "function_call")
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
				hasForeignFunctionCall = true;
			}
		}

		return matches;
	}

	private JsonObject BuildFunctionCallOutput(JsonObject functionCall)
	{
		var callId = functionCall["call_id"]?.GetValue<string>() ?? string.Empty;
		var argumentsString = functionCall["arguments"]?.GetValue<string>();
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
			["type"] = "function_call_output",
			["call_id"] = callId,
			["output"] = resultArray.ToJsonString(),
		};
	}

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

	private static void AppendFunctionCallTurn(JsonObject requestBody, List<JsonObject> functionCalls)
	{
		if (requestBody["input"] is not JsonArray input)
		{
			return;
		}

		// Echo each function_call item from the response into input — preserving call_id, name,
		// and arguments — so the follow-up function_call_output items have a matching call_id to
		// bind to.
		foreach (var call in functionCalls)
		{
			input.Add(call.DeepClone());
		}
	}

	private static void AppendFunctionCallOutputs(JsonObject requestBody, List<JsonObject> outputs)
	{
		if (requestBody["input"] is not JsonArray input)
		{
			return;
		}

		foreach (var output in outputs)
		{
			input.Add(output);
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

	[LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "CCR (OpenAI Responses) fulfilled {FunctionCallCount} tophat_retrieve function_call(s) on iteration {Iteration} (localId={LocalId}).")]
	private static partial void LogIteration(ILogger logger, string localId, int iteration, int functionCallCount);

	[LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "CCR (OpenAI Responses) hit iteration budget {MaxIterations}; returning last response as-is (localId={LocalId}).")]
	private static partial void LogBudgetExhausted(ILogger logger, string localId, int maxIterations);
}
