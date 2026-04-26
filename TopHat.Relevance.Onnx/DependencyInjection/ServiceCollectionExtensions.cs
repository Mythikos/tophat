using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TopHat.Relevance;

namespace TopHat.Relevance.Onnx.DependencyInjection;

/// <summary>
/// Extension methods for registering the ONNX-backed semantic relevance scorer.
/// </summary>
public static class ServiceCollectionExtensions
{
	/// <summary>
	/// Adds <see cref="OnnxScorer"/> to the <see cref="IRelevanceScorer"/> pool consumed by
	/// <c>JsonContextCompressorTransform</c>. When BM25 (via <c>AddTopHatBm25Relevance</c>) or
	/// any other scorer is also registered, the compressor automatically fuses them via
	/// normalized-sum fusion.
	/// </summary>
	/// <param name="services">The service collection.</param>
	/// <param name="model">A model descriptor produced by <see cref="OnnxRelevanceModels"/>.</param>
	/// <param name="configure">Optional callback to tweak batch size, failure mode, or execution provider.</param>
	public static IServiceCollection AddTopHatOnnxRelevance(this IServiceCollection services, OnnxModelDescriptor model, Action<OnnxScorerOptions>? configure = null)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentNullException.ThrowIfNull(model);

		services.AddOptions<OnnxScorerOptions>()
			.Configure(opts =>
			{
				opts.Model = model;
				configure?.Invoke(opts);
			})
			.Validate(opts => opts.Model is not null, $"{nameof(OnnxScorerOptions)}.{nameof(OnnxScorerOptions.Model)} must be set.")
			.Validate(opts => opts.BatchSize > 0, $"{nameof(OnnxScorerOptions)}.{nameof(OnnxScorerOptions.BatchSize)} must be greater than zero.");

		services.TryAddSingleton<OnnxScorer>();
		// Use the typed-factory overload so the descriptor's implementation type is OnnxScorer
		// (keeping TryAddEnumerable's dedup by (ServiceType, ImplementationType) working), while
		// the factory resolves through the concrete singleton — one shared 90MB model, not two.
		services.TryAddEnumerable(ServiceDescriptor.Singleton<IRelevanceScorer, OnnxScorer>(
			sp => sp.GetRequiredService<OnnxScorer>()));

		return services;
	}
}
