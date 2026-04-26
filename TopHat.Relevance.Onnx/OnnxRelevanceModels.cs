namespace TopHat.Relevance.Onnx;

/// <summary>
/// Factory methods for <see cref="OnnxModelDescriptor"/>. Each method returns a descriptor
/// pre-configured for a specific embedding model we've tested; <see cref="Custom"/> lets
/// consumers wire in any other ONNX model with explicit configuration.
/// </summary>
/// <remarks>
/// New entries are added as additional static methods. Keep descriptor fields aligned with the
/// model's own config (<c>sentence_bert_config.json</c>, <c>config.json</c>, tokenizer config)
/// — misconfiguration here produces wrong embeddings silently.
/// </remarks>
public static class OnnxRelevanceModels
{
	/// <summary>
	/// Pre-configured descriptor for
	/// <a href="https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2">sentence-transformers/all-MiniLM-L6-v2</a>.
	/// 22M parameters, 384-dim embeddings, 256-token max sequence, mean-pool + L2 normalize.
	/// Expects <c>model.onnx</c> and <c>vocab.txt</c> at the root of <paramref name="modelDirectory"/>.
	/// </summary>
	/// <param name="modelDirectory">Directory containing <c>model.onnx</c> and <c>vocab.txt</c>.</param>
	public static OnnxModelDescriptor AllMiniLML6V2(string modelDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);

		return new OnnxModelDescriptor
		{
			ModelPath = Path.Combine(modelDirectory, "model.onnx"),
			VocabPath = Path.Combine(modelDirectory, "vocab.txt"),
			MaxSequenceLength = 256,
			EmbeddingDim = 384,
			LowerCase = true,
			Pooling = PoolingStrategy.Mean,
			NormalizeEmbeddings = true,
		};
	}

	/// <summary>
	/// Descriptor for a user-supplied model we haven't pre-validated. The caller provides every
	/// configuration value explicitly. Prefer a named factory (e.g. <see cref="AllMiniLML6V2"/>)
	/// when one exists.
	/// </summary>
	/// <param name="modelPath">Path to the <c>.onnx</c> file.</param>
	/// <param name="vocabPath">Path to the WordPiece <c>vocab.txt</c>.</param>
	/// <param name="maxSequenceLength">Model's max input length in tokens.</param>
	/// <param name="embeddingDim">Dimensionality of the produced embeddings.</param>
	/// <param name="lowerCase">Whether the BERT tokenizer should lowercase input. Defaults to <c>true</c>.</param>
	/// <param name="pooling">How to collapse per-token embeddings. Defaults to <see cref="PoolingStrategy.Mean"/>.</param>
	/// <param name="normalizeEmbeddings">Whether to L2-normalize the pooled output. Defaults to <c>true</c>.</param>
	public static OnnxModelDescriptor Custom(string modelPath, string vocabPath, int maxSequenceLength, int embeddingDim, bool lowerCase = true, PoolingStrategy pooling = PoolingStrategy.Mean, bool normalizeEmbeddings = true)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(vocabPath);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxSequenceLength, 0);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(embeddingDim, 0);

		return new OnnxModelDescriptor
		{
			ModelPath = modelPath,
			VocabPath = vocabPath,
			MaxSequenceLength = maxSequenceLength,
			EmbeddingDim = embeddingDim,
			LowerCase = lowerCase,
			Pooling = pooling,
			NormalizeEmbeddings = normalizeEmbeddings,
		};
	}
}
