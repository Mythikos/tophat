namespace TopHat.Relevance.Onnx;

/// <summary>
/// Selects the ONNX Runtime execution provider used by the scorer. Phase 1 wires
/// <see cref="Cpu"/> only; other values are reserved for later phases and will throw at session
/// construction if selected.
/// </summary>
public enum OnnxExecutionProvider
{
	/// <summary>Default CPU execution provider — portable, no external dependency.</summary>
	Cpu = 0,

	/// <summary>
	/// DirectML (Windows GPU). Reserved — not wired in phase 1. Will throw at session
	/// construction if selected.
	/// </summary>
	DirectML = 1,

	/// <summary>
	/// CUDA (NVIDIA GPU). Reserved — not wired in phase 1. Will throw at session construction
	/// if selected.
	/// </summary>
	Cuda = 2,
}
