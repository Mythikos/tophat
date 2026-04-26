using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TopHat.Tokenizers;

namespace TopHat.Tokenizers.OpenAI.DependencyInjection;

/// <summary>
/// Extension methods for registering <see cref="OpenAITiktokenTokenizer"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
	/// <summary>
	/// Adds <see cref="OpenAITiktokenTokenizer"/> to the <see cref="ITokenizer"/> pool. Provides
	/// bit-exact token counts for OpenAI-targeted requests (<c>OpenAIChatCompletions</c>,
	/// <c>OpenAIResponses</c>) — overriding the chars/4 fallback for those targets. Other targets
	/// continue to use whichever tokenizer is registered for them (or the chars/4 default).
	/// </summary>
	/// <remarks>
	/// Idempotent via <see cref="ServiceCollectionDescriptorExtensions.TryAddEnumerable(IServiceCollection, ServiceDescriptor)"/>.
	/// Pull in this package alongside the data sub-packages it depends on
	/// (<c>Microsoft.ML.Tokenizers.Data.Cl100kBase</c>, <c>Microsoft.ML.Tokenizers.Data.O200kBase</c>);
	/// they're transitive references so they come along automatically.
	/// </remarks>
	public static IServiceCollection AddTopHatOpenAITokenizer(this IServiceCollection services)
	{
		ArgumentNullException.ThrowIfNull(services);
		services.TryAddEnumerable(ServiceDescriptor.Singleton<ITokenizer, OpenAITiktokenTokenizer>());
		return services;
	}
}
