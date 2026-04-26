namespace TopHat.Relevance.Onnx;

/// <summary>
/// Describes a specific ONNX embedding model on disk along with the tokenization and pooling
/// settings required to run it. Produced by factory methods on <see cref="OnnxRelevanceModels"/>.
/// </summary>
/// <remarks>
/// This type is immutable by design — <c>init</c>-only setters with <c>required</c> on the
/// fields that have no sane default. Construct via <c>new ()</c> with object initializer, or
/// use <see cref="OnnxRelevanceModels.AllMiniLML6V2"/> / <see cref="OnnxRelevanceModels.Custom"/>.
/// </remarks>
public sealed class OnnxModelDescriptor
{
	/// <summary>Absolute or relative path to the <c>.onnx</c> model file.</summary>
	public required string ModelPath { get; init; }

	/// <summary>
	/// Absolute or relative path to the WordPiece vocabulary file (<c>vocab.txt</c>) used by the
	/// BERT tokenizer. Required — we do not fall back to any bundled vocabulary.
	/// </summary>
	public required string VocabPath { get; init; }

	/// <summary>
	/// Maximum input sequence length in tokens — inputs are truncated at this bound before
	/// inference. Should match the model's training configuration (256 for MiniLM-L6-v2,
	/// 512 for full BERT-base, etc.).
	/// </summary>
	public required int MaxSequenceLength { get; init; }

	/// <summary>
	/// Dimensionality of the produced embeddings (384 for MiniLM-L6-v2, 768 for MPNet, etc.).
	/// Used to pre-size buffers and validate model output shape.
	/// </summary>
	public required int EmbeddingDim { get; init; }

	/// <summary>
	/// Whether the BERT tokenizer should lowercase input. Matches the model's <c>do_lower_case</c>
	/// training setting. MiniLM-L6-v2 uses <c>true</c>; cased models like <c>bert-base-cased</c>
	/// require <c>false</c>.
	/// </summary>
	public bool LowerCase { get; init; } = true;

	/// <summary>
	/// How to collapse the per-token output tensor into a single embedding. Mean is standard
	/// for sentence-transformers models.
	/// </summary>
	public PoolingStrategy Pooling { get; init; } = PoolingStrategy.Mean;

	/// <summary>
	/// Whether to L2-normalize the pooled embedding. Required when consumers use cosine similarity
	/// (which is the case for the ONNX scorer). Disable only if you need raw magnitudes.
	/// </summary>
	public bool NormalizeEmbeddings { get; init; } = true;
}
