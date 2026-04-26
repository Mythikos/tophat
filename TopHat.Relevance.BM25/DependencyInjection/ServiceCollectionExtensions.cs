using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TopHat.Relevance;

namespace TopHat.Relevance.BM25.DependencyInjection;

/// <summary>
/// Extension methods for registering the BM25 keyword relevance scorer.
/// </summary>
public static class ServiceCollectionExtensions
{
	/// <summary>
	/// Adds <see cref="BM25Scorer"/> to the <see cref="IRelevanceScorer"/> pool consumed by
	/// <c>JsonContextCompressorTransform</c>. Safe to call alongside <c>AddTopHatOnnxRelevance</c>
	/// — multiple scorers in the pool are automatically fused via normalized-sum fusion.
	/// </summary>
	/// <remarks>
	/// Uses <see cref="ServiceCollectionDescriptorExtensions.TryAddEnumerable(IServiceCollection, ServiceDescriptor)"/>
	/// so the registration is idempotent — calling this method twice does not register BM25 twice.
	/// </remarks>
	public static IServiceCollection AddTopHatBm25Relevance(this IServiceCollection services)
	{
		ArgumentNullException.ThrowIfNull(services);
		services.TryAddEnumerable(ServiceDescriptor.Singleton<IRelevanceScorer, BM25Scorer>());
		return services;
	}
}
