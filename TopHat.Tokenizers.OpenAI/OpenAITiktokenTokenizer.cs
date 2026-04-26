using System.Collections.Concurrent;
using System.Text;
using Microsoft.ML.Tokenizers;
using TopHat.Providers;
using TopHat.Tokenizers;

namespace TopHat.Tokenizers.OpenAI;

/// <summary>
/// Bit-exact <see cref="ITokenizer"/> for OpenAI-targeted requests, backed by
/// <see cref="TiktokenTokenizer"/> from <c>Microsoft.ML.Tokenizers</c>. The encoding picked
/// per request follows the upstream model: <c>o200k_base</c> for gpt-4o family,
/// <c>cl100k_base</c> for gpt-4 / gpt-3.5-turbo. Local computation, no network — sub-millisecond
/// per request after the first call (vocab tables are cached after their initial load).
/// </summary>
/// <remarks>
/// <para>
/// The tokenizer is built lazily per encoding family and cached for the process lifetime.
/// Subsequent requests reuse the same instance (it's thread-safe by design).
/// </para>
/// <para>
/// The body is decoded as UTF-8 before tokenization — TopHat receives bytes from the request
/// pipeline but the tokenizer operates on strings. For typical request payloads this decode is
/// negligible relative to the tokenization cost.
/// </para>
/// </remarks>
public sealed class OpenAITiktokenTokenizer : ITokenizer
{
	// Cache tokenizers by encoding name. Lazy<T> ensures only one instance is built per encoding
	// even under concurrent first-call. Holding the Lazy itself rather than the tokenizer avoids
	// double-initialization in race conditions on first warm-up.
	private static readonly ConcurrentDictionary<string, Lazy<TiktokenTokenizer>> s_tokenizers = new();

	/// <inheritdoc/>
	public string Kind => "tiktoken";

	/// <inheritdoc/>
	public bool SupportsTarget(TopHatTarget target) => target switch
	{
		TopHatTarget.OpenAIChatCompletions => true,
		TopHatTarget.OpenAIResponses => true,
		_ => false,
	};

	/// <inheritdoc/>
	public ValueTask<int> CountTokensAsync(ReadOnlyMemory<byte> body, TopHatTarget target, string? model, Action<int>? deferredEmit, CancellationToken cancellationToken)
	{
		// Tiktoken is local and fast — always returns synchronously, deferredEmit is unused.
		if (body.IsEmpty)
		{
			return new ValueTask<int>(0);
		}

		var tokenizer = ResolveTokenizer(model);
		var text = Encoding.UTF8.GetString(body.Span);
		var count = tokenizer.CountTokens(text);
		return new ValueTask<int>(count);
	}

	private static TiktokenTokenizer ResolveTokenizer(string? model)
	{
		var encodingName = ResolveEncodingName(model);
		var lazy = s_tokenizers.GetOrAdd(encodingName, name => new Lazy<TiktokenTokenizer>(
			() => TiktokenTokenizer.CreateForEncoding(name)));
		return lazy.Value;
	}

	/// <summary>
	/// Maps an OpenAI model name to its tokenizer encoding family. Defaults to <c>o200k_base</c>
	/// (the newer family used by gpt-4o and beyond) when the model is unknown — newer models are
	/// the more common case and o200k is closer to bit-exact for them.
	/// </summary>
	private static string ResolveEncodingName(string? model)
	{
		if (string.IsNullOrEmpty(model))
		{
			return "o200k_base";
		}

		// gpt-4o family: gpt-4o, gpt-4o-mini, gpt-4o-2024-*, o1*, o3*, o4* — all use o200k.
		if (model.StartsWith("gpt-4o", StringComparison.OrdinalIgnoreCase)
			|| model.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
			|| model.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
			|| model.StartsWith("o4", StringComparison.OrdinalIgnoreCase))
		{
			return "o200k_base";
		}

		// gpt-4, gpt-3.5-turbo, text-embedding-* — cl100k.
		if (model.StartsWith("gpt-4", StringComparison.OrdinalIgnoreCase)
			|| model.StartsWith("gpt-3.5", StringComparison.OrdinalIgnoreCase)
			|| model.StartsWith("text-embedding-", StringComparison.OrdinalIgnoreCase))
		{
			return "cl100k_base";
		}

		// Unknown model — assume newer family. Better than failing or silently using a wrong encoding.
		return "o200k_base";
	}
}
