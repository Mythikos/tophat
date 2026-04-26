using System.Diagnostics.Metrics;
using TopHat.Diagnostics;

namespace TopHat.Relevance.Onnx.Diagnostics;

/// <summary>
/// Metrics emitted by the ONNX scorer. Shares the <c>TopHat</c> meter name with
/// <see cref="TopHatMetrics"/> so consumers who already call
/// <c>.AddMeter(TopHatMetrics.MeterName)</c> pick these up with no extra wiring.
/// </summary>
internal static class OnnxMetrics
{
	private static readonly string? s_assemblyVersion = typeof(OnnxMetrics).Assembly.GetName().Version?.ToString();

	internal static readonly Meter Meter = new (TopHatMetrics.MeterName, s_assemblyVersion);

	/// <summary>
	/// Counter incremented every time the ONNX scorer catches an inference exception and falls
	/// back to BM25 under <see cref="OnnxInferenceFailureMode.FallbackToBm25"/>. Tags:
	/// <c>kind</c> (exception type name) so consumers can see which failures dominate.
	/// </summary>
	internal static readonly Counter<long> FallbackTotal = Meter.CreateCounter<long>("tophat.onnx.fallback", unit: "{fallback}", description: "ONNX scorer fallbacks to BM25 after inference errors, classified by exception kind.");
}
