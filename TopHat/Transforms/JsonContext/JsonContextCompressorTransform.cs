using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Nodes;
using TopHat.Compression.CCR;
using TopHat.Diagnostics;
using TopHat.Feedback;
using TopHat.Relevance;
using TopHat.Transforms.JsonContext.Common;
using TopHat.Transforms.JsonContext.Messages;
using TopHat.Transforms.JsonContext.Strategies;
using TopHat.Transforms.JsonContext.Summarization;

namespace TopHat.Transforms.JsonContext;

/// <summary>
/// Compresses JSON tool-result content in outgoing request bodies to reduce token usage.
/// Targets Anthropic <c>/v1/messages</c> (tool_result blocks), OpenAI <c>/v1/chat/completions</c>
/// (role:"tool" messages), and OpenAI <c>/v1/responses</c> (function_call_output items).
/// Port of headroom's SmartCrusher — request-side only, stateless per request, no CCR/TOIN.
/// </summary>
/// <remarks>
/// Requires at least one <see cref="IRelevanceScorer"/> to be registered in DI — typically via
/// <c>AddTopHatBm25Relevance()</c>, <c>AddTopHatOnnxRelevance(...)</c>, or a custom singleton
/// registration. When multiple scorers are registered they are automatically fused via
/// <see cref="FusedRelevanceScorer"/> (normalized-sum fusion); a single scorer is used directly.
/// Resolving the transform with zero scorers registered throws a clear setup error.
/// </remarks>
public sealed class JsonContextCompressorTransform : IRequestTransform
{
	private readonly JsonContextCompressorOptions _options;
	private readonly IRelevanceScorer _scorer;
	private readonly IReadOnlyList<IDroppedItemsSummarizer> _summarizers;
	private readonly ICompressionContextStore? _ccrStore;
	private readonly ICompressionFeedbackStore? _feedbackStore;
	private readonly FeedbackThresholds? _feedbackThresholds;
	private readonly FeedbackOverridesConfiguration? _feedbackOverrides;

	// Rough characters-per-token estimate used for the min-tokens gate.
	private const int CharsPerToken = 4;

	public JsonContextCompressorTransform(IOptions<JsonContextCompressorOptions> options, IEnumerable<IRelevanceScorer>? scorers = null, IEnumerable<IDroppedItemsSummarizer>? summarizers = null, ICompressionContextStore? ccrStore = null, ICompressionFeedbackStore? feedbackStore = null, IOptions<FeedbackThresholds>? feedbackThresholds = null, IOptions<FeedbackOverridesConfiguration>? feedbackOverrides = null)
	{
		ArgumentNullException.ThrowIfNull(options);
		this._options = options.Value;
		this._scorer = ComposeScorer(scorers);
		this._summarizers = summarizers?.ToArray() ?? Array.Empty<IDroppedItemsSummarizer>();
		this._ccrStore = ccrStore;
		this._feedbackStore = feedbackStore;
		// FeedbackThresholds is opt-in via UseTopHatFeedbackDecisions(). If not registered,
		// _feedbackThresholds stays null and the decision layer is bypassed entirely —
		// recording still happens (purely observational) but no behavior change.
		this._feedbackThresholds = feedbackThresholds?.Value;
		// FeedbackOverridesConfiguration is opt-in via UseTopHatFeedbackOverrides(). Static
		// declarative overrides; checked alongside the store's runtime overrides.
		this._feedbackOverrides = feedbackOverrides?.Value;
	}

	private static IRelevanceScorer ComposeScorer(IEnumerable<IRelevanceScorer>? scorers)
	{
		var pool = scorers?.ToArray() ?? Array.Empty<IRelevanceScorer>();

		return pool.Length switch
		{
			0 => throw new InvalidOperationException(
				"JsonContextCompressorTransform requires at least one IRelevanceScorer to be registered. "
				+ "Call AddTopHatBm25Relevance() for keyword scoring, AddTopHatOnnxRelevance(...) for semantic scoring, "
				+ "or register a custom IRelevanceScorer singleton before AddTopHatJsonContextCompressor()."),
			1 => pool[0],
			_ => new FusedRelevanceScorer(pool),
		};
	}

	/// <inheritdoc/>
	public ValueTask InvokeAsync(RequestTransformContext context, CancellationToken cancellationToken)
	{
		if (context.Body is not JsonObject body)
		{
			return ValueTask.CompletedTask;
		}

		// Compute frozen message count (messages that precede the sliding window of non-frozen turns).
		var totalMessages = CountMessages(body, context.Target);
		var frozenCount = Math.Max(0, totalMessages - _options.UnfrozenMessageCount);

		// Locate all tool-result string slots.
		var toolResults = ToolResultLocator.Find(body, context.Target, frozenCount);

		if (toolResults.Count == 0)
		{
			RecordSkip(context, "no_tool_results");
			return ValueTask.CompletedTask;
		}

		// Extract query context once per request (expensive-ish, shared across all refs).
		var queryContext = RelevanceContextExtractor.Extract(body, context.Target);

		// CCR is opt-in: a store must be registered in DI AND the target must support tool injection.
		// Anthropic /v1/messages, OpenAI /v1/chat/completions, and OpenAI /v1/responses all have
		// orchestrators wired up; other targets fall through with no retrieval-key callback so CCR
		// is transparently disabled for them.
		var ccrActive = _ccrStore is not null && IsCCRSupportedTarget(context.Target);
		Func<IReadOnlyList<JsonNode>, string?>? ccrRegister = null;
		var anyRetrievalKeyIssued = false;

		if (ccrActive)
		{
			ccrRegister = dropped =>
			{
				if (dropped.Count == 0)
				{
					return null;
				}

				var key = Guid.NewGuid().ToString("N");
				_ccrStore!.Store(key, dropped);
				anyRetrievalKeyIssued = true;
				return key;
			};
		}

		var compressionContext = new JsonCompressionContext
		{
			QueryContext = queryContext,
			Scorer = _scorer,
			Options = _options,
			Summarizers = _summarizers,
			RegisterDroppedItemsForRetrieval = ccrRegister,
		};

		var totalCompressed = 0;
		var totalSkipped = 0;

		foreach (var toolResult in toolResults)
		{
			var text = toolResult.Text;
			var approxTokens = text.Length / CharsPerToken;

			if (approxTokens < _options.MinTokensToCrush)
			{
				totalSkipped++;
				continue;
			}

			// Resolve tool name once — used for both decision consultation (if active) and
			// compression-event recording (if a store is registered).
			string? toolName = null;
			if (this._feedbackStore is not null)
			{
				var toolUseId = ToolNameResolver.ExtractToolUseId(toolResult.Owner, context.Target);
				if (!string.IsNullOrEmpty(toolUseId))
				{
					toolName = ToolNameResolver.Resolve(body, context.Target, toolUseId);
				}
			}

			// Two-tier consultation:
			//   1. Manual override (store runtime override OR static config override) — user-declared
			//      truth, always wins regardless of whether decisions are enabled. Store wins when
			//      both are present (runtime intent supersedes static config).
			//   2. Empirical threshold-based decisions only fire when UseTopHatFeedbackDecisions()
			//      registered a thresholds instance with Enabled=true.
			if (!string.IsNullOrEmpty(toolName))
			{
				var stats = this._feedbackStore?.GetStats(toolName);
				var effectiveOverride = stats?.ManualOverride ?? FeedbackOverride.None;
				if (effectiveOverride == FeedbackOverride.None && this._feedbackOverrides is not null)
				{
					effectiveOverride = this._feedbackOverrides.GetOverride(toolName);
				}

				if (effectiveOverride == FeedbackOverride.SkipCompression)
				{
					RecordSkip(context, "feedback_skip:manual override");
					totalSkipped++;
					continue;
				}

				// Threshold-based decisions are the opt-in behavior gate. AlwaysCompress
				// override (if set) effectively forces a Standard guidance regardless of stats.
				if (this._feedbackThresholds is { Enabled: true } && effectiveOverride != FeedbackOverride.AlwaysCompress)
				{
					var guidance = FeedbackDecision.Decide(stats, this._feedbackThresholds);
					if (guidance.SkipCompression)
					{
						RecordSkip(context, $"feedback_skip:{guidance.Reason}");
						totalSkipped++;
						continue;
					}
				}
			}

			var compressedText = TryCompress(text, compressionContext);

			if (compressedText is null || compressedText == text)
			{
				totalSkipped++;
				continue;
			}

			ToolResultRewriter.Rewrite(toolResult, compressedText);
			totalCompressed++;

			// Record the compression event after a successful crush. Best-effort: skipped
			// silently when the tool name can't be resolved.
			if (this._feedbackStore is not null && !string.IsNullOrEmpty(toolName))
			{
				this._feedbackStore.RecordCompression(toolName);
			}
		}

		if (totalCompressed == 0)
		{
			if (totalSkipped == toolResults.Count)
			{
				RecordSkip(context, totalSkipped > 0 ? "below_token_threshold" : "no_compression_gain");
			}

			return ValueTask.CompletedTask;
		}

		// If any tool_result was compressed AND registered with the CCR store, make the retrieval
		// tool visible to the model on this turn. Skipped when no retrieval key was issued — e.g.,
		// array strategies that don't surface dropped items through the callback path.
		if (anyRetrievalKeyIssued)
		{
			InjectCCRTool(body, context.Target);
		}

		context.MarkMutated();

		if (context.Logger.IsEnabled(LogLevel.Debug))
		{
#pragma warning disable CA1873 // Already gated on IsEnabled.
			TopHatLogEvents.JsonContextCompressorApplied(
				context.Logger, totalCompressed, totalSkipped, context.Target, context.LocalId);
#pragma warning restore CA1873
		}

		return ValueTask.CompletedTask;
	}

	private static string? TryCompress(string text, JsonCompressionContext ctx)
	{
		// Non-JSON content passes through unchanged (headroom's SmartCrusher docstring rule).
		JsonNode? parsed;

		try
		{
			parsed = JsonNode.Parse(text);
		}
		catch (JsonException)
		{
			return null;
		}

		if (parsed is null)
		{
			return null;
		}

		var (result, wasModified) = NestedRecursionStrategy.Process(parsed, ctx);

		if (!wasModified || result is null)
		{
			return null;
		}

		return result.ToJsonString();
	}

	private static void RecordSkip(RequestTransformContext context, string reason)
	{
		if (context.Logger.IsEnabled(LogLevel.Debug))
		{
#pragma warning disable CA1873
			TopHatLogEvents.JsonContextCompressorSkipped(
				context.Logger, reason, context.Target, context.LocalId);
#pragma warning restore CA1873
		}
	}

	private static int CountMessages(JsonObject body, Providers.TopHatTarget target)
	{
		var array = target switch
		{
			Providers.TopHatTarget.AnthropicMessages => body["messages"] as JsonArray,
			Providers.TopHatTarget.OpenAIChatCompletions => body["messages"] as JsonArray,
			Providers.TopHatTarget.OpenAIResponses => body["input"] as JsonArray,
			_ => null,
		};

		return array?.Count ?? 0;
	}

	private static bool IsCCRSupportedTarget(Providers.TopHatTarget target) => target switch
	{
		Providers.TopHatTarget.AnthropicMessages => true,
		Providers.TopHatTarget.OpenAIChatCompletions => true,
		Providers.TopHatTarget.OpenAIResponses => true,
		_ => false,
	};

	private static void InjectCCRTool(JsonObject body, Providers.TopHatTarget target)
	{
		switch (target)
		{
			case Providers.TopHatTarget.AnthropicMessages:
				AnthropicCCRToolInjector.EnsureToolPresent(body);
				break;
			case Providers.TopHatTarget.OpenAIChatCompletions:
				OpenAIChatCompletionsCCRToolInjector.EnsureToolPresent(body);
				break;
			case Providers.TopHatTarget.OpenAIResponses:
				OpenAIResponsesCCRToolInjector.EnsureToolPresent(body);
				break;
		}
	}
}
