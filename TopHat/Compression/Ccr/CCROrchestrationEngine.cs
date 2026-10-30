using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using TopHat.Diagnostics;
using TopHat.Feedback;
using TopHat.Providers;

namespace TopHat.Compression.CCR;

/// <summary>Shared bounded retrieval loop; private responses must be inspected before they can reach the caller.</summary>
internal sealed partial class CCROrchestrationEngine
{
	private const string ExhaustionResult = "TopHat retrieval budget exhausted. Answer using the available context; further retrieval is unavailable for this request.";
	private readonly TopHatTarget _target;
	private readonly CCRProtocol _protocol;
	private readonly ICompressionContextStore _store;
	private readonly ICompressionFeedbackStore? _feedbackStore;

	/// <summary>Creates an orchestration engine using the provider's transcript codec and configured stores.</summary>
	public CCROrchestrationEngine(TopHatTarget target, ICompressionContextStore store, ICompressionFeedbackStore? feedbackStore)
	{
		this._target = target;
		this._protocol = new CCRProtocol(target);
		this._store = store;
		this._feedbackStore = feedbackStore;
	}

	/// <summary>Resolves private retrievals within the configured allowance and returns a sanitized terminal response.</summary>
	public async ValueTask<HttpResponseMessage> RunAsync(CCROrchestrationContext context, CCROptions options, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);
		var currentResponse = context.InitialResponse;
		try
		{
			var requestBody = context.OriginalRequest.Content is null ? null : await ParseAsync(context.OriginalRequest.Content, cancellationToken).ConfigureAwait(false);
			if (requestBody is null || CCRRequestPolicy.HasCallerToolCollision(requestBody, this._target, context.OwnsToolDefinition))
			{
				return currentResponse;
			}

			var accumulatedUsage = new JsonObject();
			// Keep the original advertisement evidence even after the exhaustion turn removes the tool.
			var advertisedInternalTool = CCRRequestPolicy.HasInternalDefinition(requestBody, this._target, context.OwnsToolDefinition);
			var requiresInspection = advertisedInternalTool;
			var hopCount = 1;
			var retrievalRounds = 0;
			var exhaustionSent = false;
			while (true)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (!currentResponse.IsSuccessStatusCode)
				{
					await this.FinalizeAsync(currentResponse, accumulatedUsage, hopCount, exhaustionSent ? "budget_exhausted" : "not_orchestratable", cancellationToken).ConfigureAwait(false);
					return currentResponse;
				}
				if (!CanInspectJson(currentResponse.Content))
				{
					if (requiresInspection)
					{
						throw this.UnsafeResponseException(currentResponse, hopCount, "not_orchestratable");
					}
					await this.FinalizeAsync(currentResponse, accumulatedUsage, hopCount, "not_orchestratable", cancellationToken).ConfigureAwait(false);
					return currentResponse;
				}

				var body = await ParseAsync(currentResponse.Content, cancellationToken).ConfigureAwait(false);
				if (body is null)
				{
					if (requiresInspection)
					{
						throw this.UnsafeResponseException(currentResponse, hopCount, "parse_failure");
					}
					await this.FinalizeAsync(currentResponse, accumulatedUsage, hopCount, exhaustionSent ? "budget_exhausted" : "parse_failure", cancellationToken).ConfigureAwait(false);
					return currentResponse;
				}
				CCRUsageMerger.Accumulate(accumulatedUsage, body["usage"] as JsonObject);
				var calls = this._protocol.Inspect(body);
				if (calls.Retrievals.Count == 0 || calls.HasForeign || calls.HasMultipleChoices || calls.HasInvalidEnvelope || exhaustionSent || !this._protocol.CanContinue(requestBody) || !this._protocol.HasValidCallIds(calls.Retrievals))
				{
					if (this._protocol.Sanitize(body, advertisedInternalTool))
					{
						ReplaceBody(currentResponse, body);
					}
					var outcome = exhaustionSent ? "budget_exhausted" : calls.HasInvalidEnvelope ? "parse_failure" : calls.HasForeign ? "foreign_tool_use" : calls.HasMultipleChoices ? "not_orchestratable" : hopCount > 1 ? "multi_hop" : "single_hop";
					await this.FinalizeAsync(currentResponse, accumulatedUsage, hopCount, outcome, cancellationToken).ConfigureAwait(false);
					return currentResponse;
				}

				var budgetExhausted = retrievalRounds >= Math.Max(0, options.MaxIterations);
				requiresInspection = true;
				this._protocol.NormalizeCallIds(requestBody, body, calls.Retrievals);
				var results = new List<JsonObject>(calls.Retrievals.Count);
				foreach (var call in calls.Retrievals)
				{
					var arguments = this._protocol.Arguments(call);
					results.Add(this._protocol.Result(call, budgetExhausted ? ExhaustionResult : this.Retrieve(arguments, options.RetrievalItemCeiling)));
					this.RecordRetrieval(arguments, requestBody, budgetExhausted);
				}
				this._protocol.AppendTurn(requestBody, body, results);
				if (budgetExhausted)
				{
					exhaustionSent = true;
					this._protocol.DisableRetrieval(requestBody);
					LogBudgetExhausted(context.Logger, context.LocalId, options.MaxIterations);
				}
				else
				{
					retrievalRounds++;
					LogIteration(context.Logger, context.LocalId, retrievalRounds, calls.Retrievals.Count);
				}

				// Internal dispatch bypasses the handler, so apply the same history invariant here.
				CCRHistoryRepair.Repair(requestBody, this._target);
				currentResponse.Dispose();
				using (var followUp = BuildFollowUpRequest(context.OriginalRequest, requestBody))
				{
					currentResponse = await context.SendUpstream(followUp, cancellationToken).ConfigureAwait(false);
				}
				hopCount++;
			}
		}
		catch
		{
			// The engine owns each response until it returns it to the handler.
			currentResponse.Dispose();
			throw;
		}
	}

	private string Retrieve(JsonObject? arguments, int itemCeiling)
	{
		var key = CCRProtocol.String(arguments?[CCRToolDefinition.RetrievalKeyField]);
		if (string.IsNullOrEmpty(key) || itemCeiling <= 0)
		{
			return "[]";
		}
		HashSet<int>? ids = null;
		if (arguments?["ids"] is JsonArray values)
		{
			ids = new HashSet<int>();
			foreach (var value in values)
			{
				if (value is JsonValue scalar && scalar.TryGetValue<int>(out var id))
				{
					ids.Add(id);
				}
			}
			if (ids.Count == 0)
			{
				ids = null;
			}
		}
		var limit = Math.Min(10, itemCeiling);
		if (arguments?["limit"] is JsonValue requested && requested.TryGetValue<int>(out var parsed))
		{
			limit = Math.Clamp(parsed, 1, itemCeiling);
		}
		return new JsonArray(this._store.Retrieve(key, ids, limit).Select(item => item.DeepClone()).ToArray()).ToJsonString();
	}

	private void RecordRetrieval(JsonObject? arguments, JsonObject request, bool exhausted)
	{
		if (this._feedbackStore is null || CCRProtocol.String(arguments?[CCRToolDefinition.RetrievalKeyField]) is not { Length: > 0 } key)
		{
			return;
		}
		var name = ToolNameResolver.ResolveByRetrievalKey(request, this._target, key);
		if (!string.IsNullOrEmpty(name))
		{
			var kind = exhausted ? RetrievalKind.BudgetExhausted : arguments?["ids"] is JsonArray ids && ids.Count > 0 ? RetrievalKind.Search : RetrievalKind.Full;
			this._feedbackStore.RecordRetrieval(name, kind);
		}
	}

	private ValueTask FinalizeAsync(HttpResponseMessage response, JsonObject usage, int hops, string outcome, CancellationToken cancellationToken)
	{
		this.RecordOutcome(hops, outcome);
		return hops > 1 ? CCRUsageMerger.ApplyAsync(response, usage, hops, cancellationToken) : ValueTask.CompletedTask;
	}

	private void RecordOutcome(int hops, string outcome)
	{
		var target = this._target.ToString();
		TopHatMetrics.CCROrchestrations.Add(1, new TagList { { "target", target }, { "outcome", outcome } });
		TopHatMetrics.CCRHops.Record(hops, new TagList { { "target", target } });
	}

	private HttpRequestException UnsafeResponseException(HttpResponseMessage response, int hops, string outcome)
	{
		this.RecordOutcome(hops, outcome);
		response.Dispose();
		return new HttpRequestException("CCR cannot safely inspect the successful response after private tool use; an inspectable JSON object is required.");
	}

	private static bool CanInspectJson(HttpContent content)
	{
		var mediaType = content.Headers.ContentType?.MediaType;
		return mediaType is null || string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)
			|| mediaType.StartsWith("application/", StringComparison.OrdinalIgnoreCase) && mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
	}

	private static async ValueTask<JsonObject?> ParseAsync(HttpContent content, CancellationToken cancellationToken)
	{
		try
		{
			return JsonNode.Parse(await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false)) as JsonObject;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static void ReplaceBody(HttpResponseMessage response, JsonObject body)
	{
		var replacement = new ByteArrayContent(Encoding.UTF8.GetBytes(body.ToJsonString()));
		foreach (var header in response.Content.Headers)
		{
			if (!string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
			{
				replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
			}
		}
		response.Content.Dispose();
		response.Content = replacement;
	}

	private static HttpRequestMessage BuildFollowUpRequest(HttpRequestMessage original, JsonObject body)
	{
		var request = new HttpRequestMessage(original.Method, original.RequestUri)
		{
			Version = original.Version, VersionPolicy = original.VersionPolicy,
			Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
		};
		foreach (var header in original.Headers)
		{
			request.Headers.TryAddWithoutValidation(header.Key, header.Value);
		}
		if (original.Content is not null)
		{
			foreach (var header in original.Content.Headers)
			{
				if (!string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase) && !string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
				{
					request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
				}
			}
		}
		return request;
	}

	[LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "CCR fulfilled {ToolUseCount} retrieval call(s) on iteration {Iteration} (localId={LocalId}).")]
	private static partial void LogIteration(ILogger logger, string localId, int iteration, int toolUseCount);

	[LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "CCR reached iteration budget {MaxIterations}; issuing the final exhaustion completion (localId={LocalId}).")]
	private static partial void LogBudgetExhausted(ILogger logger, string localId, int maxIterations);
}
