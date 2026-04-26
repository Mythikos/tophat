using Microsoft.ML.Tokenizers;

namespace TopHat.Relevance.Onnx.Internals;

/// <summary>
/// Thin wrapper over <see cref="BertTokenizer"/> that produces fixed-shape batches
/// (<c>input_ids</c>, <c>attention_mask</c>, <c>token_type_ids</c>) ready to feed directly into an
/// ONNX BERT-style embedding session.
/// </summary>
/// <remarks>
/// Single-segment tokenization only — we do not do NSP-style pair inputs. Special tokens
/// (<c>[CLS]</c>, <c>[SEP]</c>) are added manually after truncating the body to
/// <c>MaxSequenceLength - 2</c>, so the final sequence never exceeds the configured bound.
/// </remarks>
internal sealed class BertTokenization
{
	private readonly BertTokenizer _tokenizer;
	private readonly int _maxSequenceLength;
	private readonly int _clsId;
	private readonly int _sepId;
	private readonly int _padId;

	public BertTokenization(string vocabPath, bool lowerCase, int maxSequenceLength)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(vocabPath);
		ArgumentOutOfRangeException.ThrowIfLessThan(maxSequenceLength, 3);

		if (!File.Exists(vocabPath))
		{
			throw new FileNotFoundException($"WordPiece vocabulary file not found: {vocabPath}", vocabPath);
		}

		var options = new BertOptions
		{
			LowerCaseBeforeTokenization = lowerCase,
		};

		_tokenizer = BertTokenizer.Create(vocabPath, options);
		_maxSequenceLength = maxSequenceLength;
		_clsId = _tokenizer.ClassificationTokenId;
		_sepId = _tokenizer.SeparatorTokenId;
		_padId = _tokenizer.PaddingTokenId;
	}

	/// <summary>
	/// Tokenize a batch of texts and pack them into flat row-major arrays shaped
	/// <c>[BatchSize * SequenceLength]</c>. Padding uses the tokenizer's pad token id;
	/// <c>attention_mask</c> marks real tokens with 1 and pads with 0.
	/// </summary>
	public TokenizedBatch Tokenize(IReadOnlyList<string> texts)
	{
		ArgumentNullException.ThrowIfNull(texts);

		if (texts.Count == 0)
		{
			return new TokenizedBatch(Array.Empty<long>(), Array.Empty<long>(), Array.Empty<long>(), 0, 0);
		}

		var bodyBudget = _maxSequenceLength - 2;
		var perRow = new List<int[]>(texts.Count);
		var maxLength = 0;

		foreach (var text in texts)
		{
			var body = _tokenizer.EncodeToIds(
				text ?? string.Empty,
				addSpecialTokens: false,
				considerPreTokenization: true,
				considerNormalization: true);

			var bodyLen = Math.Min(body.Count, bodyBudget);
			var row = new int[bodyLen + 2];
			row[0] = _clsId;
			for (var i = 0; i < bodyLen; i++)
			{
				row[i + 1] = body[i];
			}
			row[bodyLen + 1] = _sepId;

			perRow.Add(row);
			if (row.Length > maxLength)
			{
				maxLength = row.Length;
			}
		}

		var batchSize = perRow.Count;
		var seqLength = maxLength;
		var inputIds = new long[batchSize * seqLength];
		var attentionMask = new long[batchSize * seqLength];
		var tokenTypeIds = new long[batchSize * seqLength];

		for (var b = 0; b < batchSize; b++)
		{
			var row = perRow[b];
			var rowOffset = b * seqLength;
			for (var t = 0; t < row.Length; t++)
			{
				inputIds[rowOffset + t] = row[t];
				attentionMask[rowOffset + t] = 1;
			}
			for (var t = row.Length; t < seqLength; t++)
			{
				inputIds[rowOffset + t] = _padId;
			}
		}

		return new TokenizedBatch(inputIds, attentionMask, tokenTypeIds, batchSize, seqLength);
	}
}

/// <summary>
/// Flat row-major tokenization output sized <c>[BatchSize * SequenceLength]</c>, ready to wrap
/// in an ONNX <c>DenseTensor&lt;long&gt;</c> with shape <c>[BatchSize, SequenceLength]</c>.
/// </summary>
internal readonly record struct TokenizedBatch(
	long[] InputIds,
	long[] AttentionMask,
	long[] TokenTypeIds,
	int BatchSize,
	int SequenceLength);
