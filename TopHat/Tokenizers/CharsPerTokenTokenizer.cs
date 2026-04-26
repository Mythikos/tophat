using TopHat.Providers;

namespace TopHat.Tokenizers;

/// <summary>
/// Default <see cref="ITokenizer"/> shipped in TopHat core. Returns
/// <c>body.Length / <see cref="CharsPerToken"/></c> as a token count — fast, dependency-free,
/// and approximate. Used when no provider-specific tokenizer is registered.
/// </summary>
/// <remarks>
/// <para>
/// The chars/4 conversion is the standard "good enough" approximation for English text but
/// systematically overestimates for JSON-heavy bodies (where tokens are denser than chars/4).
/// For accurate cost-transparency metrics, install a provider-specific tokenizer package:
/// <c>TopHat.Tokenizers.OpenAi</c> (Microsoft.ML.Tokenizers, bit-exact, local) or
/// <c>TopHat.Tokenizers.Anthropic</c> (count_tokens HTTP endpoint, bit-exact, network cost).
/// </para>
/// <para>
/// Emitted metrics carry the tag <c>tokenizer_kind=chars_per_token</c> so dashboards can
/// filter out approximations when authoritative counts are required.
/// </para>
/// </remarks>
public sealed class CharsPerTokenTokenizer : ITokenizer
{
	/// <summary>
	/// Characters per token in the chars/4 approximation. Public so other components (e.g., the
	/// JSON context compressor's min-tokens gate) can use the same constant.
	/// </summary>
	public const int CharsPerToken = 4;

	/// <inheritdoc/>
	public string Kind => "chars_per_token";

	/// <inheritdoc/>
	public bool SupportsTarget(TopHatTarget target) => true;

	/// <inheritdoc/>
	public ValueTask<int> CountTokensAsync(ReadOnlyMemory<byte> body, TopHatTarget target, string? model, Action<int>? deferredEmit, CancellationToken cancellationToken)
	{
		// Synchronous; deferredEmit is unused.
		return new ValueTask<int>(body.Length / CharsPerToken);
	}
}
