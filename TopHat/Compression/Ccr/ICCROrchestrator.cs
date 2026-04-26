using Microsoft.Extensions.Logging;
using TopHat.Providers;

namespace TopHat.Compression.CCR;

/// <summary>
/// Orchestrates the CCR retrieval loop on the response side: when the model calls the injected
/// <c>tophat_retrieve</c> tool, the orchestrator intercepts the tool_use, fulfils it from
/// <see cref="ICompressionContextStore"/>, and re-dispatches the conversation upstream until the
/// model emits a final non-tool response or the iteration budget is exhausted. The client only
/// ever sees the terminal response — the retrieval hops are invisible.
/// </summary>
/// <remarks>
/// Implementations are provider-specific (Anthropic / OpenAI) because the tool-use detection,
/// follow-up message construction, and streaming-vs-buffered response shapes differ. Phase 1
/// ships an Anthropic implementation only; OpenAI follows in Phase 2.
/// </remarks>
public interface ICCROrchestrator
{
	/// <summary>The target this orchestrator handles. Handlers select the right implementation by target.</summary>
	TopHatTarget Target { get; }

	/// <summary>
	/// Runs the retrieval loop if the initial response contains a <c>tophat_retrieve</c> tool_use.
	/// Returns the final response the caller should see — either the original response unchanged
	/// (no retrieval needed / unsupported response shape / budget exhausted) or a new response
	/// produced by one or more follow-up upstream calls.
	/// </summary>
	ValueTask<HttpResponseMessage> OrchestrateAsync(CCROrchestrationContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Per-invocation state carried into <see cref="ICCROrchestrator.OrchestrateAsync"/>.
/// </summary>
public sealed class CCROrchestrationContext
{
	public CCROrchestrationContext(
		HttpRequestMessage originalRequest,
		HttpResponseMessage initialResponse,
		Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendUpstream,
		string localId,
		ILogger logger)
	{
		OriginalRequest = originalRequest;
		InitialResponse = initialResponse;
		SendUpstream = sendUpstream;
		LocalId = localId;
		Logger = logger;
	}

	/// <summary>The request as it was dispatched to upstream (post-transform).</summary>
	public HttpRequestMessage OriginalRequest { get; }

	/// <summary>
	/// The response to the initial request. The orchestrator inspects this for a retrieval
	/// tool_use; if none is found, returns it unchanged.
	/// </summary>
	public HttpResponseMessage InitialResponse { get; }

	/// <summary>
	/// Dispatches a follow-up request to upstream. Bound to <c>base.SendAsync</c> by the handler
	/// so the follow-up traverses any remaining delegating handlers without re-entering
	/// <c>TopHatHandler</c>'s own pre-processing.
	/// </summary>
	public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> SendUpstream { get; }

	/// <summary>Correlation ID matching the originating request for logs.</summary>
	public string LocalId { get; }

	/// <summary>Scoped logger.</summary>
	public ILogger Logger { get; }
}
