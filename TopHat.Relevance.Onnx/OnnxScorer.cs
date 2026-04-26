using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TopHat.Relevance;
using TopHat.Relevance.BM25;
using TopHat.Relevance.Onnx.Diagnostics;
using TopHat.Relevance.Onnx.Internals;

namespace TopHat.Relevance.Onnx;

/// <summary>
/// Semantic <see cref="IRelevanceScorer"/> backed by a BERT-style ONNX embedding model plus
/// mean-pooling and cosine similarity. Register via <c>AddTopHatOnnxRelevance</c>.
/// </summary>
/// <remarks>
/// <para>
/// Pipeline per <see cref="ScoreBatch"/> call: tokenize the context + every item, run the ONNX
/// session in batches of <see cref="OnnxScorerOptions.BatchSize"/>, mean-pool over the attention
/// mask (L2-normalize when the descriptor opts in), and cosine-compare each item against the
/// context embedding.
/// </para>
/// <para>
/// Inference failures are handled according to <see cref="OnnxScorerOptions.InferenceFailureMode"/>
/// — either rethrown (<see cref="OnnxInferenceFailureMode.Throw"/>) or silently downgraded to
/// BM25 (<see cref="OnnxInferenceFailureMode.FallbackToBm25"/>, the default). Configuration-time
/// errors (missing model, bad descriptor, unsupported execution provider) always throw up-front
/// in the constructor regardless of this setting.
/// </para>
/// </remarks>
public sealed partial class OnnxScorer : IRelevanceScorer, IDisposable
{
	private readonly BertTokenization _tokenizer;
	private readonly OnnxSession _session;
	private readonly OnnxModelDescriptor _model;
	private readonly OnnxInferenceFailureMode _failureMode;
	private readonly int _batchSize;
	private readonly BM25Scorer _fallback;
	private readonly ILogger<OnnxScorer>? _logger;

	public OnnxScorer(IOptions<OnnxScorerOptions> options, ILogger<OnnxScorer>? logger = null)
	{
		ArgumentNullException.ThrowIfNull(options);

		var opts = options.Value ?? throw new ArgumentException("OnnxScorerOptions.Value was null.", nameof(options));
		_model = opts.Model ?? throw new InvalidOperationException(
			$"{nameof(OnnxScorerOptions)}.{nameof(OnnxScorerOptions.Model)} must be set before resolving the scorer.");

		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(opts.BatchSize, 0);

		_tokenizer = new BertTokenization(_model.VocabPath, _model.LowerCase, _model.MaxSequenceLength);
		_session = new OnnxSession(_model.ModelPath, _model.EmbeddingDim, opts.ExecutionProvider);
		_failureMode = opts.InferenceFailureMode;
		_batchSize = opts.BatchSize;
		_fallback = new BM25Scorer();
		_logger = logger;
	}

	/// <inheritdoc/>
	public RelevanceScore Score(string item, string context)
	{
		return ScoreBatch(new[] { item }, context)[0];
	}

	/// <inheritdoc/>
	public IReadOnlyList<RelevanceScore> ScoreBatch(IReadOnlyList<string> items, string context)
	{
		ArgumentNullException.ThrowIfNull(items);

		if (items.Count == 0)
		{
			return Array.Empty<RelevanceScore>();
		}

		if (string.IsNullOrEmpty(context))
		{
			return items.Select(_ => new RelevanceScore(0.0, "ONNX: empty context")).ToArray();
		}

		try
		{
			return ScoreBatchCore(items, context);
		}
		catch (Exception ex) when (_failureMode == OnnxInferenceFailureMode.FallbackToBm25)
		{
			OnnxMetrics.FallbackTotal.Add(1, new KeyValuePair<string, object?>("kind", ex.GetType().Name));

			if (_logger is not null)
			{
				LogOnnxFallback(_logger, items.Count, ex);
			}

			return _fallback.ScoreBatch(items, context);
		}
	}

	[LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "ONNX inference failed; falling back to BM25 for batch of {Count} items.")]
	private static partial void LogOnnxFallback(ILogger logger, int count, Exception exception);

	private RelevanceScore[] ScoreBatchCore(IReadOnlyList<string> items, string context)
	{
		var queryEmbedding = EmbedOne(context);
		var results = new RelevanceScore[items.Count];
		var scratch = new float[_batchSize];

		for (var start = 0; start < items.Count; start += _batchSize)
		{
			var count = Math.Min(_batchSize, items.Count - start);
			var slice = new string[count];

			for (var i = 0; i < count; i++)
			{
				slice[i] = items[start + i] ?? string.Empty;
			}

			var docEmbeddings = EmbedBatch(slice);
			var scores = scratch.AsSpan(0, count);
			CosineSimilarity.Batch(queryEmbedding, docEmbeddings, _model.EmbeddingDim, scores);

			for (var i = 0; i < count; i++)
			{
				var clamped = Math.Clamp(scores[i], 0f, 1f);
				results[start + i] = new RelevanceScore(clamped, $"ONNX cosine: {clamped:F3}");
			}
		}

		return results;
	}

	private float[] EmbedOne(string text)
	{
		var tokens = _tokenizer.Tokenize(new[] { text });
		var hidden = _session.Run(tokens);
		return EmbeddingPooler.MeanPool(hidden, tokens.AttentionMask, _model.NormalizeEmbeddings);
	}

	private float[] EmbedBatch(IReadOnlyList<string> texts)
	{
		var tokens = _tokenizer.Tokenize(texts);
		var hidden = _session.Run(tokens);
		return EmbeddingPooler.MeanPool(hidden, tokens.AttentionMask, _model.NormalizeEmbeddings);
	}

	public void Dispose() => _session.Dispose();
}
