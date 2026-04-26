using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace TopHat.Relevance.Onnx.Internals;

/// <summary>
/// Owns a single <see cref="InferenceSession"/> for a BERT-style embedding model and runs one
/// tokenized batch at a time. The session is loaded once in the constructor and reused across
/// calls — callers are expected to hold one <see cref="OnnxSession"/> for the scorer's lifetime.
/// </summary>
/// <remarks>
/// <para>
/// Expects a model with three inputs (<c>input_ids</c>, <c>attention_mask</c>, <c>token_type_ids</c>,
/// all int64 tensors of shape <c>[batch, seq]</c>) and a float output of shape
/// <c>[batch, seq, embedding_dim]</c> — the per-token hidden states. The first float output is
/// used, which matches the sentence-transformers ONNX export convention (<c>last_hidden_state</c>).
/// </para>
/// <para>
/// Output data is copied into a caller-owned <see cref="float"/> array before the underlying
/// <c>DisposableNamedOnnxValue</c>s are released, so the caller does not inherit any lifetime
/// constraints from ORT's native memory.
/// </para>
/// </remarks>
internal sealed class OnnxSession : IDisposable
{
	private readonly InferenceSession _session;
	private readonly int _embeddingDim;

	public OnnxSession(string modelPath, int embeddingDim, OnnxExecutionProvider provider)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(embeddingDim, 0);

		if (!File.Exists(modelPath))
		{
			throw new FileNotFoundException($"ONNX model file not found: {modelPath}", modelPath);
		}

		var options = new SessionOptions();
		switch (provider)
		{
			case OnnxExecutionProvider.Cpu:
				break;
			case OnnxExecutionProvider.DirectML:
			case OnnxExecutionProvider.Cuda:
				options.Dispose();
				throw new NotSupportedException(
					$"Execution provider '{provider}' is reserved and not wired in phase 1. Use {nameof(OnnxExecutionProvider)}.{nameof(OnnxExecutionProvider.Cpu)}.");
			default:
				options.Dispose();
				throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown execution provider.");
		}

		_session = new InferenceSession(modelPath, options);
		_embeddingDim = embeddingDim;
	}

	/// <summary>
	/// Run the model for one tokenized batch. Returns a flat row-major
	/// <c>[batch * seq * embedding_dim]</c> buffer plus its dimensions so the caller can pool.
	/// </summary>
	public OnnxEmbeddingOutput Run(TokenizedBatch batch)
	{
		if (batch.BatchSize == 0 || batch.SequenceLength == 0)
		{
			return new OnnxEmbeddingOutput(Array.Empty<float>(), 0, 0, _embeddingDim);
		}

		var shape = new[] { batch.BatchSize, batch.SequenceLength };
		var inputIdsTensor = new DenseTensor<long>(batch.InputIds, shape);
		var attentionMaskTensor = new DenseTensor<long>(batch.AttentionMask, shape);
		var tokenTypeIdsTensor = new DenseTensor<long>(batch.TokenTypeIds, shape);

		var inputs = new List<NamedOnnxValue>
		{
			NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
			NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor),
			NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIdsTensor),
		};

		using (var results = _session.Run(inputs))
		{
			var first = results.First(r => r.Value is DenseTensor<float>);
			var tensor = (DenseTensor<float>)first.Value;

			var dims = tensor.Dimensions;
			if (dims.Length != 3 || dims[0] != batch.BatchSize || dims[1] != batch.SequenceLength || dims[2] != _embeddingDim)
			{
				throw new InvalidOperationException(
					$"ONNX output '{first.Name}' has shape [{string.Join(',', dims.ToArray())}], expected [{batch.BatchSize},{batch.SequenceLength},{_embeddingDim}].");
			}

			var flat = new float[batch.BatchSize * batch.SequenceLength * _embeddingDim];
			tensor.Buffer.Span.CopyTo(flat);

			return new OnnxEmbeddingOutput(flat, batch.BatchSize, batch.SequenceLength, _embeddingDim);
		}
	}

	public void Dispose() => _session.Dispose();
}

/// <summary>
/// Row-major per-token hidden states shaped <c>[BatchSize * SequenceLength * EmbeddingDim]</c>,
/// ready for mean pooling against an attention mask.
/// </summary>
internal readonly record struct OnnxEmbeddingOutput(
	float[] HiddenStates,
	int BatchSize,
	int SequenceLength,
	int EmbeddingDim);
