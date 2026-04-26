namespace TopHat.Relevance.Onnx.Internals;

/// <summary>
/// Collapses per-token hidden states into one embedding per sequence using mean pooling over the
/// attention-masked positions, with optional L2 normalization. Mirrors the standard
/// sentence-transformers pooling step.
/// </summary>
internal static class EmbeddingPooler
{
	/// <summary>
	/// Mean-pool <paramref name="output"/> over the positions where <paramref name="attentionMask"/>
	/// is 1, producing a row-major <c>[BatchSize * EmbeddingDim]</c> buffer. When
	/// <paramref name="normalize"/> is <c>true</c>, each row is L2-normalized in place so cosine
	/// similarity reduces to a dot product downstream.
	/// </summary>
	public static float[] MeanPool(OnnxEmbeddingOutput output, ReadOnlySpan<long> attentionMask, bool normalize)
	{
		if (output.BatchSize == 0)
		{
			return Array.Empty<float>();
		}

		var batch = output.BatchSize;
		var seq = output.SequenceLength;
		var dim = output.EmbeddingDim;
		var hidden = output.HiddenStates;
		var pooled = new float[batch * dim];

		for (var b = 0; b < batch; b++)
		{
			var rowOffset = b * dim;
			long validCount = 0;

			for (var t = 0; t < seq; t++)
			{
				if (attentionMask[b * seq + t] == 0)
				{
					continue;
				}

				validCount++;
				var tokenOffset = (b * seq + t) * dim;
				for (var d = 0; d < dim; d++)
				{
					pooled[rowOffset + d] += hidden[tokenOffset + d];
				}
			}

			var divisor = validCount == 0 ? 1f : (float)validCount;
			for (var d = 0; d < dim; d++)
			{
				pooled[rowOffset + d] /= divisor;
			}

			if (normalize)
			{
				double sumSq = 0;
				for (var d = 0; d < dim; d++)
				{
					var v = pooled[rowOffset + d];
					sumSq += v * v;
				}

				if (sumSq > 0)
				{
					var inv = (float)(1.0 / Math.Sqrt(sumSq));
					for (var d = 0; d < dim; d++)
					{
						pooled[rowOffset + d] *= inv;
					}
				}
			}
		}

		return pooled;
	}
}
