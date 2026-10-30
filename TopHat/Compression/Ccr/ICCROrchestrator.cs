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
/// Provider adapters share a bounded orchestration loop while preserving the Anthropic,
/// Chat Completions, and Responses transcript shapes. Streaming requests must be excluded
/// from CCR injection by the request boundary. Successful responses that cannot be safely
/// inspected after private tool advertisement or use fail locally with an HTTP request exception.
/// Unsupported responses without private tools and upstream HTTP errors retain pass-through behavior.
/// </remarks>
public interface ICCROrchestrator
{
	/// <summary>The target this orchestrator handles. Handlers select the right implementation by target.</summary>
	TopHatTarget Target { get; }

	/// <summary>
	/// Runs the retrieval loop if the initial response contains a <c>tophat_retrieve</c> tool_use.
	/// Returns the final response the caller should see — either the original response unchanged
	/// (no retrieval needed) or a response produced by bounded
	/// follow-up calls. Mixed and exhausted JSON responses remove TopHat-owned calls while
	/// retaining caller-owned tools. One final completion communicates retrieval exhaustion.
	/// </summary>
	ValueTask<HttpResponseMessage> OrchestrateAsync(CCROrchestrationContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Per-invocation state carried into <see cref="ICCROrchestrator.OrchestrateAsync"/>.
/// </summary>
public sealed class CCROrchestrationContext
{
	/// <summary>Provenance supplied by the handler for definitions injected during this request.</summary>
	internal bool OwnsToolDefinition { get; init; }

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
