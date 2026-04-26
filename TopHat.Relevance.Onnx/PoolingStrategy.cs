namespace TopHat.Relevance.Onnx;

/// <summary>
/// Strategy for collapsing per-token embeddings into a single sentence embedding.
/// </summary>
public enum PoolingStrategy
{
	/// <summary>
	/// Mean-pool token embeddings over positions where <c>attention_mask == 1</c>. This is the
	/// standard pooling used by sentence-transformers' MiniLM, MPNet, and most modern embedding
	/// models. Phase 1 supports this strategy only.
	/// </summary>
	Mean = 0,
}
