using System.Text.RegularExpressions;
using TopHat.Relevance;

namespace TopHat.Relevance.BM25;

/// <summary>
/// BM25 keyword relevance scorer. Zero external dependencies, instant execution.
/// Excellent for exact ID/UUID matching. Port of headroom's BM25Scorer (bm25.py).
/// </summary>
/// <remarks>
/// BM25 formula per query term q:
///     IDF(q) * (f(q,D) * (k1 + 1)) / (f(q,D) + k1 * (1 - b + b * |D| / avgdl))
/// Where f(q,D) = term frequency in document, |D| = document length, avgdl = average doc length.
///
/// Limitations: no semantic understanding ("errors" won't match "failed" without exact term).
/// For semantic relevance, use TopHat.Relevance.Onnx with a hybrid scorer.
/// </remarks>
public sealed class BM25Scorer : IRelevanceScorer
{
	// Matches UUIDs, 4+-digit numeric IDs, and alphanumeric tokens — same as headroom.
	private static readonly Regex s_tokenPattern = new(
		@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}" +
		@"|\b\d{4,}\b" +
		@"|[a-zA-Z0-9_]+",
		RegexOptions.Compiled, TimeSpan.FromSeconds(1));

	private const double DefaultK1 = 1.5;
	private const double DefaultB = 0.75;
	private const double DefaultMaxScore = 10.0;
	private const int LongTokenMinLength = 8;
	private const double LongTokenBonus = 0.3;
	private const int MaxMatchedTermsReturned = 10;

	private readonly double _k1;
	private readonly double _b;
	private readonly double _maxScore;

	public BM25Scorer(double k1 = DefaultK1, double b = DefaultB, double maxScore = DefaultMaxScore)
	{
		_k1 = k1;
		_b = b;
		_maxScore = maxScore;
	}

	/// <inheritdoc/>
	public RelevanceScore Score(string item, string context)
	{
		var itemTokens = Tokenize(item);
		var contextTokens = Tokenize(context);

		var (rawScore, matched) = ComputeBm25(itemTokens, contextTokens, avgDocLen: null);
		return BuildScore(rawScore, matched);
	}

	/// <inheritdoc/>
	public IReadOnlyList<RelevanceScore> ScoreBatch(IReadOnlyList<string> items, string context)
	{
		var contextTokens = Tokenize(context);

		if (contextTokens.Count == 0)
		{
			return items.Select(_ => new RelevanceScore(0.0, "BM25: empty context")).ToArray();
		}

		var allItemTokens = items.Select(Tokenize).ToArray();
		var avgLen = allItemTokens.Length > 0
			? allItemTokens.Average(t => (double)t.Count)
			: 1.0;

		var results = new RelevanceScore[items.Count];

		for (var idx = 0; idx < allItemTokens.Length; idx++)
		{
			var (rawScore, matched) = ComputeBm25(allItemTokens[idx], contextTokens, avgDocLen: avgLen);
			results[idx] = BuildScore(rawScore, matched);
		}

		return results;
	}

	private (double Score, List<string> Matched) ComputeBm25(
		IReadOnlyList<string> docTokens,
		IReadOnlyList<string> queryTokens,
		double? avgDocLen)
	{
		if (docTokens.Count == 0 || queryTokens.Count == 0)
		{
			return (0.0, []);
		}

		var docLen = docTokens.Count;
		var avgdl = avgDocLen ?? docLen;

		if (avgdl <= 0)
		{
			avgdl = 1.0;
		}

		// Build term frequency maps.
		var docFreq = new Dictionary<string, int>(StringComparer.Ordinal);

		foreach (var token in docTokens)
		{
			docFreq[token] = docFreq.GetValueOrDefault(token) + 1;
		}

		var queryFreq = new Dictionary<string, int>(StringComparer.Ordinal);

		foreach (var token in queryTokens)
		{
			queryFreq[token] = queryFreq.GetValueOrDefault(token) + 1;
		}

		var score = 0.0;
		var matched = new List<string>();
		// IDF is simplified for single-corpus scoring: log(2) constant.
		const double idf = 0.693147180559945; // Math.Log(2.0)

		foreach (var (term, queryCount) in queryFreq)
		{
			if (!docFreq.TryGetValue(term, out var f))
			{
				continue;
			}

			matched.Add(term);

			var numerator = f * (_k1 + 1.0);
			var denominator = f + _k1 * (1.0 - _b + _b * docLen / avgdl);
			var termScore = idf * numerator / denominator;
			score += termScore * queryCount;
		}

		return (score, matched);
	}

	private RelevanceScore BuildScore(double rawScore, List<string> matched)
	{
		var normalized = Math.Min(1.0, rawScore / _maxScore);

		// Bonus for long exact matches (UUIDs, long IDs) — these are high-value.
		if (matched.Any(t => t.Length >= LongTokenMinLength))
		{
			normalized = Math.Min(1.0, normalized + LongTokenBonus);
		}

		var matchCount = matched.Count;
		string reason;

		if (matchCount == 0)
		{
			reason = "BM25: no term matches";
		}
		else if (matchCount == 1)
		{
			reason = $"BM25: matched '{matched[0]}'";
		}
		else
		{
			var preview = string.Join(", ", matched.Take(3));
			reason = matchCount > 3
				? $"BM25: matched {matchCount} terms ({preview}...)"
				: $"BM25: matched {matchCount} terms ({preview})";
		}

		return new RelevanceScore(normalized, reason, matched.Take(MaxMatchedTermsReturned).ToArray());
	}

	internal static IReadOnlyList<string> Tokenize(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return [];
		}

		var matches = s_tokenPattern.Matches(text.ToLowerInvariant());
		var tokens = new List<string>(matches.Count);

		foreach (Match m in matches)
		{
			tokens.Add(m.Value);
		}

		return tokens;
	}
}
