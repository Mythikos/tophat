namespace TopHat.Relevance.Onnx;

/// <summary>
/// Controls how the ONNX scorer handles errors during inference (tokenization, model
/// execution, pooling). Configuration-time errors (missing files, malformed options) always
/// throw regardless of this setting.
/// </summary>
public enum OnnxInferenceFailureMode
{
	/// <summary>
	/// Propagate the underlying exception to the caller. Use this when silent degradation to a
	/// weaker scorer is unacceptable — e.g., during development or in pipelines that should fail
	/// loudly on model regressions.
	/// </summary>
	Throw = 0,

	/// <summary>
	/// Log a warning, increment the fallback counter, and transparently use BM25 scoring for
	/// the failing call. The transform still produces a result. Recommended default for
	/// production resilience.
	/// </summary>
	FallbackToBm25 = 1,
}
