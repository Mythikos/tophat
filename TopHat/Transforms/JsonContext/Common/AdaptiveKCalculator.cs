using System.Security.Cryptography;
using System.Text;

namespace TopHat.Transforms.JsonContext.Common;

/// <summary>
/// Computes the optimal number of items to keep using information saturation detection.
/// Port of headroom's adaptive_sizer.compute_optimal_k (three-tier Kneedle algorithm).
/// </summary>
/// <remarks>
/// Three-tier decision:
///   Tier 1 (fast path): trivial cases / near-duplicate detection via SimHash.
///   Tier 2 (standard):  Kneedle on unique bigram coverage curve.
///   Tier 3 (validation): zlib compression ratio sanity check.
/// </remarks>
internal static class AdaptiveKCalculator
{
	private const int TrivialN = 8;
	private const int NearTotallyRedundantUniqueCount = 3;
	private const double KneeMinDeviation = 0.05;
	private const double ZlibValidationTolerance = 0.15;
	private const double ZlibIncreaseFactor = 1.2;
	private const int ZlibMinContentBytes = 200;
	private const int SimHashThreshold = 3;  // Hamming distance ≤ 3 → near-duplicate

	/// <summary>
	/// Returns the optimal number of items to keep.
	/// </summary>
	/// <param name="items">Items in importance order (most important first).</param>
	/// <param name="bias">Multiplier on the knee. &gt;1 = keep more, &lt;1 = compress harder.</param>
	/// <param name="minK">Minimum items to keep.</param>
	/// <param name="maxK">Maximum items to keep. Null = no cap beyond item count.</param>
	public static int Compute(IReadOnlyList<string> items, double bias = 1.0, int minK = 3, int? maxK = null)
	{
		var n = items.Count;
		var effectiveMax = maxK.HasValue ? Math.Min(maxK.Value, n) : n;

		// Tier 1: fast path — tiny sets.
		if (n <= TrivialN)
		{
			return n;
		}

		// Tier 1: near-total redundancy.
		var uniqueCount = CountUniqueSimHash(items);

		if (uniqueCount <= NearTotallyRedundantUniqueCount)
		{
			return Math.Min(effectiveMax, Math.Max(minK, uniqueCount));
		}

		// Tier 2: Kneedle on unique bigram coverage curve.
		var curve = ComputeUniqueBigramCurve(items);
		var knee = FindKnee(curve);

		var diversityRatio = (double)uniqueCount / n;

		if (knee is null)
		{
			// No saturation found — scale by diversity.
			var keepFraction = 0.3 + 0.7 * diversityRatio;
			knee = Math.Max(minK, (int)(n * keepFraction));
		}
		else if (diversityRatio > 0.7)
		{
			// Knee may be a weak signal when items are highly diverse.
			var diversityFloor = Math.Max(minK, (int)(n * (0.3 + 0.7 * diversityRatio)));
			knee = Math.Max(knee.Value, diversityFloor);
		}

		var k = Math.Max(minK, (int)(knee.Value * bias));
		k = Math.Min(k, effectiveMax);

		// Tier 3: zlib compression ratio validation.
		k = ValidateWithZlib(items, k, effectiveMax);

		return Math.Max(minK, Math.Min(k, effectiveMax));
	}

	internal static int? FindKnee(IReadOnlyList<int> curve)
	{
		var n = curve.Count;

		if (n < 3)
		{
			return null;
		}

		var yMin = (double)curve[0];
		var yMax = (double)curve[^1];

		if (yMax == yMin)
		{
			// Flat curve — all items identical.
			return 1;
		}

		var xRange = (double)(n - 1);
		var yRange = yMax - yMin;

		var maxDiff = -1.0;
		int? kneeIdx = null;

		for (var idx = 0; idx < n; idx++)
		{
			var xNorm = idx / xRange;
			var yNorm = (curve[idx] - yMin) / yRange;
			var diff = yNorm - xNorm;

			if (diff > maxDiff)
			{
				maxDiff = diff;
				kneeIdx = idx;
			}
		}

		if (maxDiff < KneeMinDeviation)
		{
			return null;
		}

		return kneeIdx.HasValue ? kneeIdx.Value + 1 : null;
	}

	internal static List<int> ComputeUniqueBigramCurve(IReadOnlyList<string> items)
	{
		var seenBigrams = new HashSet<(string, string)>();
		var curve = new List<int>(items.Count);

		foreach (var item in items)
		{
			var words = item.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);

			if (words.Length < 2)
			{
				seenBigrams.Add((words.Length > 0 ? words[0] : string.Empty, string.Empty));
			}
			else
			{
				for (var idx = 0; idx < words.Length - 1; idx++)
				{
					seenBigrams.Add((words[idx], words[idx + 1]));
				}
			}

			curve.Add(seenBigrams.Count);
		}

		return curve;
	}

	private static int CountUniqueSimHash(IReadOnlyList<string> items)
	{
		if (items.Count == 0)
		{
			return 0;
		}

		var fingerprints = new List<ulong>(items.Count);

		foreach (var item in items)
		{
			fingerprints.Add(SimHash(item));
		}

		var clusters = new List<ulong>();

		foreach (var fp in fingerprints)
		{
			var matched = false;

			foreach (var rep in clusters)
			{
				if (HammingDistance(fp, rep) <= SimHashThreshold)
				{
					matched = true;
					break;
				}
			}

			if (!matched)
			{
				clusters.Add(fp);
			}
		}

		return clusters.Count;
	}

	private static ulong SimHash(string text)
	{
		var v = new int[64];
		var lower = text.ToLowerInvariant();

		// Character 4-grams hashed via MD5 (first 8 bytes → uint64) — same approach as headroom.
		// MD5 is intentionally used here as a fast non-cryptographic fingerprinting function.
#pragma warning disable CA5351 // Do not use broken cryptographic algorithms — MD5 is used as a fingerprint hash, not for cryptography.
		var maxGrams = Math.Max(1, lower.Length - 3);

		for (var idx = 0; idx < maxGrams; idx++)
		{
			var gram = lower.Substring(idx, Math.Min(4, lower.Length - idx));
			var hash = MD5.HashData(Encoding.UTF8.GetBytes(gram));
			var h = BitConverter.ToUInt64(hash, 0);

			for (var bit = 0; bit < 64; bit++)
			{
				v[bit] += (h & (1UL << bit)) != 0 ? 1 : -1;
			}
		}
#pragma warning restore CA5351

		ulong fingerprint = 0;

		for (var bit = 0; bit < 64; bit++)
		{
			if (v[bit] > 0)
			{
				fingerprint |= 1UL << bit;
			}
		}

		return fingerprint;
	}

	private static int HammingDistance(ulong a, ulong b)
	{
		// Count differing bits — portable popcount.
		var diff = a ^ b;
		var count = 0;

		while (diff != 0)
		{
			count += (int)(diff & 1);
			diff >>= 1;
		}

		return count;
	}

	private static int ValidateWithZlib(IReadOnlyList<string> items, int k, int maxK)
	{
		if (k >= items.Count || k >= maxK)
		{
			return k;
		}

		var fullText = Encoding.UTF8.GetBytes(string.Join("\n", items));

		if (fullText.Length < ZlibMinContentBytes)
		{
			return k;
		}

		var subsetText = Encoding.UTF8.GetBytes(string.Join("\n", items.Take(k)));

		var fullCompressed = ZlibCompress(fullText);
		var subsetCompressed = ZlibCompress(subsetText);

		var fullRatio = (double)fullCompressed / fullText.Length;
		var subsetRatio = subsetText.Length > 0 ? (double)subsetCompressed / subsetText.Length : 1.0;

		if (Math.Abs(fullRatio - subsetRatio) > ZlibValidationTolerance)
		{
			return Math.Min((int)(k * ZlibIncreaseFactor), maxK);
		}

		return k;
	}

	private static int ZlibCompress(byte[] data)
	{
		using var ms = new System.IO.MemoryStream();
		using (var deflate = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.Fastest))
		{
			deflate.Write(data, 0, data.Length);
		}
		return (int)ms.Length;
	}
}
