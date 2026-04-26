using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TopHat.Compression.CCR;

namespace TopHat.Compression.CCR.DependencyInjection;

/// <summary>
/// Extension methods for registering Compression Context Retrieval — the response-side loop that
/// fulfils the model's <c>tophat_retrieve</c> tool calls from the dropped-items store so the
/// model can recover items the compressor elided.
/// </summary>
public static class ServiceCollectionExtensions
{
	/// <summary>
	/// Registers the CCR store, options, and provider-specific orchestrators. Idempotent — safe
	/// to call more than once. Must be paired with <c>AddTopHatJsonContextCompressor()</c> for
	/// CCR to have any effect: the compressor writes dropped items into the store on the request
	/// side; the orchestrator reads them on the response side.
	/// </summary>
	/// <remarks>
	/// Registers an orchestrator per supported target: Anthropic <c>/v1/messages</c>, OpenAI
	/// <c>/v1/chat/completions</c>, and OpenAI <c>/v1/responses</c>. Each one understands its own
	/// tool-call detection and follow-up message construction. Targets without a registered
	/// orchestrator (e.g., OpenAI batches, Anthropic count_tokens) fall through unchanged.
	/// </remarks>
	public static IServiceCollection AddTopHatCCR(this IServiceCollection services, Action<CCROptions>? configure = null)
	{
		ArgumentNullException.ThrowIfNull(services);

		if (configure is not null)
		{
			services.Configure(configure);
		}
		else
		{
			services.AddOptions<CCROptions>();
		}

		services.TryAddSingleton<ICompressionContextStore, InMemoryCompressionContextStore>();
		services.TryAddEnumerable(ServiceDescriptor.Singleton<ICCROrchestrator, AnthropicCCROrchestrator>());
		services.TryAddEnumerable(ServiceDescriptor.Singleton<ICCROrchestrator, OpenAIChatCompletionsCCROrchestrator>());
		services.TryAddEnumerable(ServiceDescriptor.Singleton<ICCROrchestrator, OpenAIResponsesCCROrchestrator>());

		return services;
	}
}
