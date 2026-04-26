using System.Numerics;

namespace TopHat.Relevance.Onnx.Internals;

/// <summary>
/// SIMD-accelerated cosine similarity for comparing one query embedding against a batch of
/// document embeddings. Hot path for the ONNX scorer — called once per candidate document per
/// transform invocation.
/// </summary>
internal static class CosineSimilarity
{
	/// <summary>
	/// Score each document in <paramref name="docs"/> against <paramref name="query"/> and write
	/// the cosine similarity into <paramref name="scores"/>. Each document is laid out contiguously
	/// in row-major order; <paramref name="docs"/>.Length must equal
	/// <paramref name="scores"/>.Length * <paramref name="embeddingDim"/>.
	/// </summary>
	/// <remarks>
	/// When the inputs are L2-normalized (the default for the ONNX scorer) the magnitude
	/// denominators are both 1 and this reduces to a dot product, but we still compute the full
	/// formula so unnormalized embeddings also produce correct results.
	/// </remarks>
	public static void Batch(ReadOnlySpan<float> query, ReadOnlySpan<float> docs, int embeddingDim, Span<float> scores)
	{
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(embeddingDim, 0);

		if (query.Length != embeddingDim)
		{
			throw new ArgumentException($"Query length {query.Length} does not match embeddingDim {embeddingDim}.", nameof(query));
		}

		if (docs.Length != scores.Length * embeddingDim)
		{
			throw new ArgumentException(
				$"docs.Length ({docs.Length}) must equal scores.Length ({scores.Length}) * embeddingDim ({embeddingDim}).",
				nameof(docs));
		}

		var queryMagnitude = Magnitude(query);
		if (queryMagnitude == 0f)
		{
			scores.Clear();
			return;
		}

		for (var i = 0; i < scores.Length; i++)
		{
			var doc = docs.Slice(i * embeddingDim, embeddingDim);
			var (dot, docMagnitude) = DotAndMagnitude(query, doc);

			scores[i] = docMagnitude == 0f ? 0f : dot / (queryMagnitude * docMagnitude);
		}
	}

	private static float Magnitude(ReadOnlySpan<float> v)
	{
		var width = Vector<float>.Count;
		var sumSqVec = Vector<float>.Zero;
		var i = 0;

		for (; i <= v.Length - width; i += width)
		{
			var chunk = new Vector<float>(v.Slice(i, width));
			sumSqVec += chunk * chunk;
		}

		var sumSq = Vector.Dot(sumSqVec, Vector<float>.One);
		for (; i < v.Length; i++)
		{
			sumSq += v[i] * v[i];
		}

		return MathF.Sqrt(sumSq);
	}

	private static (float Dot, float Magnitude) DotAndMagnitude(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
	{
		var width = Vector<float>.Count;
		var dotVec = Vector<float>.Zero;
		var sumSqBVec = Vector<float>.Zero;
		var i = 0;

		for (; i <= a.Length - width; i += width)
		{
			var av = new Vector<float>(a.Slice(i, width));
			var bv = new Vector<float>(b.Slice(i, width));
			dotVec += av * bv;
			sumSqBVec += bv * bv;
		}

		var dot = Vector.Dot(dotVec, Vector<float>.One);
		var sumSqB = Vector.Dot(sumSqBVec, Vector<float>.One);
		for (; i < a.Length; i++)
		{
			dot += a[i] * b[i];
			sumSqB += b[i] * b[i];
		}

		return (dot, MathF.Sqrt(sumSqB));
	}
}
