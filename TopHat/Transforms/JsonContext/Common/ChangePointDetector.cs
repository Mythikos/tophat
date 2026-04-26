namespace TopHat.Transforms.JsonContext.Common;

/// <summary>
/// Detects indices in a numeric series where values change significantly.
/// Items adjacent to change points are preserved during compression.
/// Port of headroom's SmartCrusher._detect_change_points (window=5, variance-based).
/// </summary>
internal static class ChangePointDetector
{
	private const int DefaultWindow = 5;

	/// <summary>
	/// Returns 0-based indices of change points in <paramref name="values"/>.
	/// A change point is an index where the sliding-window mean before vs. after differs
	/// by more than <paramref name="varianceThreshold"/> standard deviations from the overall series.
	/// Nearby change points (within <paramref name="window"/> of each other) are de-duplicated.
	/// Returns empty when the series is too short or has zero variance.
	/// </summary>
	public static IReadOnlyList<int> Detect(IReadOnlyList<double> values, double varianceThreshold = 2.0, int window = DefaultWindow)
	{
		if (values.Count < window * 2)
		{
			return [];
		}

		var overallStd = ComputeStdDev(values);

		if (overallStd == 0.0)
		{
			return [];
		}

		var threshold = varianceThreshold * overallStd;
		var changePoints = new List<int>();

		for (var idx = window; idx < values.Count - window; idx++)
		{
			var beforeMean = ComputeMean(values, idx - window, window);
			var afterMean = ComputeMean(values, idx, window);

			if (Math.Abs(afterMean - beforeMean) > threshold)
			{
				changePoints.Add(idx);
			}
		}

		if (changePoints.Count == 0)
		{
			return [];
		}

		// Deduplicate nearby change points (keep the first in each cluster).
		var deduped = new List<int> { changePoints[0] };

		for (var idx = 1; idx < changePoints.Count; idx++)
		{
			if (changePoints[idx] - deduped[^1] > window)
			{
				deduped.Add(changePoints[idx]);
			}
		}

		return deduped;
	}

	private static double ComputeMean(IReadOnlyList<double> values, int start, int count)
	{
		var sum = 0.0;

		for (var idx = start; idx < start + count && idx < values.Count; idx++)
		{
			sum += values[idx];
		}

		return sum / count;
	}

	private static double ComputeStdDev(IReadOnlyList<double> values)
	{
		if (values.Count <= 1)
		{
			return 0.0;
		}

		var mean = 0.0;

		foreach (var v in values)
		{
			mean += v;
		}

		mean /= values.Count;

		var variance = 0.0;

		foreach (var v in values)
		{
			var diff = v - mean;
			variance += diff * diff;
		}

		// Sample standard deviation (N-1).
		variance /= values.Count - 1;
		return Math.Sqrt(variance);
	}
}
