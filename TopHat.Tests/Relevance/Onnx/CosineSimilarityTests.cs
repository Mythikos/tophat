using TopHat.Relevance.Onnx.Internals;
using Xunit;

namespace TopHat.Tests.Relevance.Onnx;

public sealed class CosineSimilarityTests
{
	[Fact]
	public void Batch_IdenticalNormalizedVectors_ScoresOne()
	{
		var v = Normalize(new float[] { 0.2f, -0.5f, 0.8f, 0.1f, 0.3f, -0.4f, 0.6f, 0.05f });
		var docs = (float[])v.Clone();
		var scores = new float[1];

		CosineSimilarity.Batch(v, docs, embeddingDim: 8, scores);

		Assert.Equal(1f, scores[0], precision: 5);
	}

	[Fact]
	public void Batch_OppositeVectors_ScoresNegativeOne()
	{
		var v = Normalize(new float[] { 0.2f, -0.5f, 0.8f, 0.1f, 0.3f, -0.4f, 0.6f, 0.05f });
		var docs = v.Select(x => -x).ToArray();
		var scores = new float[1];

		CosineSimilarity.Batch(v, docs, embeddingDim: 8, scores);

		Assert.Equal(-1f, scores[0], precision: 5);
	}

	[Fact]
	public void Batch_OrthogonalVectors_ScoresZero()
	{
		var query = new float[] { 1f, 0f, 0f, 0f };
		var docs = new float[] { 0f, 1f, 0f, 0f };
		var scores = new float[1];

		CosineSimilarity.Batch(query, docs, embeddingDim: 4, scores);

		Assert.Equal(0f, scores[0], precision: 5);
	}

	[Fact]
	public void Batch_MultipleDocs_ScoresEach()
	{
		// Query along [1,0,0,0]; three docs at 0 deg, 60 deg (cos=0.5), 90 deg (cos=0).
		var query = new float[] { 1f, 0f, 0f, 0f };
		var docs = new float[]
		{
			1f, 0f, 0f, 0f,
			0.5f, 0.866025403f, 0f, 0f,
			0f, 1f, 0f, 0f,
		};
		var scores = new float[3];

		CosineSimilarity.Batch(query, docs, embeddingDim: 4, scores);

		Assert.Equal(1f, scores[0], precision: 4);
		Assert.Equal(0.5f, scores[1], precision: 4);
		Assert.Equal(0f, scores[2], precision: 4);
	}

	[Fact]
	public void Batch_ZeroMagnitudeQuery_AllScoresZero()
	{
		var query = new float[] { 0f, 0f, 0f, 0f };
		var docs = new float[] { 1f, 2f, 3f, 4f };
		var scores = new float[] { 99f };

		CosineSimilarity.Batch(query, docs, embeddingDim: 4, scores);

		Assert.Equal(0f, scores[0]);
	}

	[Fact]
	public void Batch_LargeDim_MatchesScalarBaseline()
	{
		// 384-dim: ensure SIMD path (which chunks by Vector<float>.Count) agrees with scalar dot.
		const int dim = 384;
		var rng = new Random(1234);
		var query = RandomVector(rng, dim);
		var doc = RandomVector(rng, dim);
		var scores = new float[1];

		CosineSimilarity.Batch(query, doc, embeddingDim: dim, scores);

		var expected = ScalarCosine(query, doc);
		Assert.Equal(expected, scores[0], precision: 4);
	}

	private static float[] Normalize(float[] v)
	{
		double sumSq = 0;
		foreach (var x in v)
		{
			sumSq += x * x;
		}

		var inv = (float)(1.0 / Math.Sqrt(sumSq));
		return v.Select(x => x * inv).ToArray();
	}

	private static float[] RandomVector(Random rng, int dim)
	{
		var v = new float[dim];

		for (var i = 0; i < dim; i++)
		{
			v[i] = (float)(rng.NextDouble() * 2 - 1);
		}

		return v;
	}

	private static float ScalarCosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
	{
		double dot = 0, aSq = 0, bSq = 0;

		for (var i = 0; i < a.Length; i++)
		{
			dot += a[i] * b[i];
			aSq += a[i] * a[i];
			bSq += b[i] * b[i];
		}

		return (float)(dot / (Math.Sqrt(aSq) * Math.Sqrt(bSq)));
	}
}
