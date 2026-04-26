namespace TopHat.Relevance.Onnx;

/// <summary>
/// Options controlling the ONNX scorer behavior — which model, how to batch, how to
/// handle runtime failures. Bound via <c>AddTopHatOnnxRelevance</c> DI registration.
/// </summary>
public sealed class OnnxScorerOptions
{
	/// <summary>The model this scorer will load. Set by <c>AddTopHatOnnxRelevance</c>.</summary>
	public OnnxModelDescriptor? Model { get; set; }

	/// <summary>
	/// How inference-time exceptions are handled. Defaults to transparent BM25 fallback for
	/// production resilience; set to <see cref="OnnxInferenceFailureMode.Throw"/> for loud
	/// failures during development or when fallback is unacceptable.
	/// </summary>
	public OnnxInferenceFailureMode InferenceFailureMode { get; set; } = OnnxInferenceFailureMode.FallbackToBm25;

	/// <summary>
	/// Number of sequences processed per ONNX inference call. Larger batches amortize session
	/// overhead but consume more memory. 32 is a reasonable default for CPU on MiniLM-sized
	/// models; raise for throughput, lower for latency-sensitive workloads.
	/// </summary>
	public int BatchSize { get; set; } = 32;

	/// <summary>
	/// Which ONNX Runtime execution provider to use. Phase 1 supports <see cref="OnnxExecutionProvider.Cpu"/>
	/// only; other values throw at session construction until wired in later phases.
	/// </summary>
	public OnnxExecutionProvider ExecutionProvider { get; set; } = OnnxExecutionProvider.Cpu;
}
