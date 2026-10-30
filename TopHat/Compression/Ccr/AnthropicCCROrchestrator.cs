using Microsoft.Extensions.Options;
using TopHat.Feedback;
using TopHat.Providers;

namespace TopHat.Compression.CCR;

/// <summary>Runs bounded, private compression retrieval for Anthropic Messages responses.</summary>
/// <remarks>Mixed turns return caller-owned calls only. Budget exhaustion permits one final completion and removes any repeated internal calls.</remarks>
public sealed partial class AnthropicCCROrchestrator : ICCROrchestrator
{
	private readonly CCROrchestrationEngine _engine;
	private readonly IOptions<CCROptions> _options;

	/// <summary>Creates an orchestrator backed by the configured dropped-item store and retrieval budget.</summary>
	public AnthropicCCROrchestrator(ICompressionContextStore store, IOptions<CCROptions> options, ICompressionFeedbackStore? feedbackStore = null)
	{
		ArgumentNullException.ThrowIfNull(store);
		ArgumentNullException.ThrowIfNull(options);
		this._options = options;
		this._engine = new CCROrchestrationEngine(this.Target, store, feedbackStore);
	}

	/// <inheritdoc/>
	public TopHatTarget Target => TopHatTarget.AnthropicMessages;

	/// <inheritdoc/>
	public ValueTask<HttpResponseMessage> OrchestrateAsync(CCROrchestrationContext context, CancellationToken cancellationToken) => this._engine.RunAsync(context, this._options.Value, cancellationToken);
}
