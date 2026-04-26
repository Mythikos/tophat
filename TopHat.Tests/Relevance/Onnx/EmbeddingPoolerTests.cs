using TopHat.Relevance.Onnx.Internals;
using Xunit;

namespace TopHat.Tests.Relevance.Onnx;

public sealed class EmbeddingPoolerTests
{
	[Fact]
	public void MeanPool_AveragesOnlyMaskedPositions()
	{
		// batch=1, seq=3, dim=2. Valid tokens at positions 0 and 1; position 2 is padded.
		// Expected mean = ([1,2] + [3,4]) / 2 = [2,3].
		var hidden = new float[]
		{
			1f, 2f,
			3f, 4f,
			99f, 99f,
		};
		var output = new OnnxEmbeddingOutput(hidden, BatchSize: 1, SequenceLength: 3, EmbeddingDim: 2);
		var mask = new long[] { 1, 1, 0 };

		var pooled = EmbeddingPooler.MeanPool(output, mask, normalize: false);

		Assert.Equal(2f, pooled[0], precision: 5);
		Assert.Equal(3f, pooled[1], precision: 5);
	}

	[Fact]
	public void MeanPool_L2Normalizes_WhenRequested()
	{
		// batch=1, seq=1, dim=3 — mean equals the single token [3,4,0]; ||[3,4,0]|| = 5; normalized = [0.6, 0.8, 0].
		var hidden = new float[] { 3f, 4f, 0f };
		var output = new OnnxEmbeddingOutput(hidden, BatchSize: 1, SequenceLength: 1, EmbeddingDim: 3);
		var mask = new long[] { 1 };

		var pooled = EmbeddingPooler.MeanPool(output, mask, normalize: true);

		Assert.Equal(0.6f, pooled[0], precision: 4);
		Assert.Equal(0.8f, pooled[1], precision: 4);
		Assert.Equal(0f, pooled[2], precision: 4);
	}

	[Fact]
	public void MeanPool_MultiRowBatch_PoolsIndependently()
	{
		// batch=2, seq=2, dim=2. Row 0 fully valid, row 1 only first position valid.
		var hidden = new float[]
		{
			1f, 0f,
			3f, 0f,

			10f, 10f,
			99f, 99f,
		};
		var output = new OnnxEmbeddingOutput(hidden, BatchSize: 2, SequenceLength: 2, EmbeddingDim: 2);
		var mask = new long[]
		{
			1, 1,
			1, 0,
		};

		var pooled = EmbeddingPooler.MeanPool(output, mask, normalize: false);

		Assert.Equal(2f, pooled[0], precision: 5);
		Assert.Equal(0f, pooled[1], precision: 5);
		Assert.Equal(10f, pooled[2], precision: 5);
		Assert.Equal(10f, pooled[3], precision: 5);
	}

	[Fact]
	public void MeanPool_AllPositionsMasked_YieldsZeroVector_WithoutNaN()
	{
		var hidden = new float[] { 5f, 5f };
		var output = new OnnxEmbeddingOutput(hidden, BatchSize: 1, SequenceLength: 1, EmbeddingDim: 2);
		var mask = new long[] { 0 };

		var pooled = EmbeddingPooler.MeanPool(output, mask, normalize: true);

		Assert.Equal(0f, pooled[0]);
		Assert.Equal(0f, pooled[1]);
		Assert.False(float.IsNaN(pooled[0]));
		Assert.False(float.IsNaN(pooled[1]));
	}
}
