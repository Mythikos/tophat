using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TopHat.Relevance;
using TopHat.Relevance.BM25;
using TopHat.Relevance.BM25.DependencyInjection;
using TopHat.Relevance.Onnx;
using TopHat.Relevance.Onnx.DependencyInjection;
using Xunit;

namespace TopHat.Tests.Relevance.Onnx;

public sealed class ServiceCollectionExtensionsTests
{
	[Fact]
	public void AddTopHatOnnxRelevance_BindsOptionsFromDescriptor()
	{
		var services = new ServiceCollection();
		var descriptor = OnnxRelevanceModels.Custom("model.onnx", "vocab.txt", maxSequenceLength: 128, embeddingDim: 384);

		services.AddTopHatOnnxRelevance(descriptor, opts =>
		{
			opts.BatchSize = 64;
			opts.InferenceFailureMode = OnnxInferenceFailureMode.Throw;
		});

		var provider = services.BuildServiceProvider();
		var opts = provider.GetRequiredService<IOptions<OnnxScorerOptions>>().Value;

		Assert.Same(descriptor, opts.Model);
		Assert.Equal(64, opts.BatchSize);
		Assert.Equal(OnnxInferenceFailureMode.Throw, opts.InferenceFailureMode);
	}

	[Fact]
	public void AddTopHatOnnxRelevance_AddsToScorerPool_DoesNotOverrideBm25()
	{
		var services = new ServiceCollection();
		services.AddTopHatBm25Relevance();

		var descriptor = OnnxRelevanceModels.Custom("model.onnx", "vocab.txt", maxSequenceLength: 128, embeddingDim: 384);
		services.AddTopHatOnnxRelevance(descriptor);

		// Both scorers must be present in the IRelevanceScorer pool so the compressor fuses them.
		var implTypes = services
			.Where(sd => sd.ServiceType == typeof(IRelevanceScorer))
			.Select(sd => sd.ImplementationType ?? sd.ImplementationFactory?.Method.ReturnType)
			.ToArray();

		Assert.Contains(typeof(BM25Scorer), implTypes);
		Assert.Contains(typeof(OnnxScorer), implTypes);
		Assert.Equal(2, implTypes.Length);
	}

	[Fact]
	public void AddTopHatOnnxRelevance_CalledTwice_RegistersOnnxOnlyOnce()
	{
		var services = new ServiceCollection();
		var descriptor = OnnxRelevanceModels.Custom("model.onnx", "vocab.txt", maxSequenceLength: 128, embeddingDim: 384);

		services.AddTopHatOnnxRelevance(descriptor);
		services.AddTopHatOnnxRelevance(descriptor);

		// TryAddEnumerable deduplicates by (ServiceType, ImplementationType) — for factory-based
		// descriptors the impl type is carried on the factory's return type.
		var onnxCount = services
			.Where(sd => sd.ServiceType == typeof(IRelevanceScorer))
			.Count(sd => (sd.ImplementationType ?? sd.ImplementationFactory?.Method.ReturnType) == typeof(OnnxScorer));
		Assert.Equal(1, onnxCount);
	}

	[Fact]
	public void AddTopHatOnnxRelevance_ValidatesBatchSize()
	{
		var services = new ServiceCollection();
		var descriptor = OnnxRelevanceModels.Custom("model.onnx", "vocab.txt", maxSequenceLength: 128, embeddingDim: 384);

		services.AddTopHatOnnxRelevance(descriptor, opts => opts.BatchSize = 0);

		var provider = services.BuildServiceProvider();
		var options = provider.GetRequiredService<IOptions<OnnxScorerOptions>>();

		var ex = Assert.Throws<OptionsValidationException>(() => options.Value);
		Assert.Contains(nameof(OnnxScorerOptions.BatchSize), ex.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void AddTopHatOnnxRelevance_NullModel_Throws()
	{
		var services = new ServiceCollection();
		Assert.Throws<ArgumentNullException>(() => services.AddTopHatOnnxRelevance(null!));
	}

	[Fact]
	public void OnnxRelevanceModels_AllMiniLML6V2_BuildsExpectedDescriptor()
	{
		var descriptor = OnnxRelevanceModels.AllMiniLML6V2("C:/models/mini");

		Assert.EndsWith("model.onnx", descriptor.ModelPath, StringComparison.OrdinalIgnoreCase);
		Assert.EndsWith("vocab.txt", descriptor.VocabPath, StringComparison.OrdinalIgnoreCase);
		Assert.Equal(256, descriptor.MaxSequenceLength);
		Assert.Equal(384, descriptor.EmbeddingDim);
		Assert.True(descriptor.LowerCase);
		Assert.True(descriptor.NormalizeEmbeddings);
		Assert.Equal(PoolingStrategy.Mean, descriptor.Pooling);
	}
}
