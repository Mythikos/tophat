using TopHat.Providers;

namespace TopHat.Tokenizers;

/// <summary>
/// Counts tokens in a request body for the purpose of cost-transparency metrics. Implementations
/// vary in accuracy and runtime cost: the in-core <c>CharsPerTokenTokenizer</c> is fast and
/// dependency-free but approximate; provider-specific packages (TopHat.Tokenizers.OpenAi,
/// TopHat.Tokenizers.Anthropic) deliver bit-exact counts at a higher implementation cost.
/// </summary>
/// <remarks>
/// <para>
/// Multiple tokenizers can be registered simultaneously. The transform pipeline picks the most
/// specific match for each request's target via <see cref="SupportsTarget"/>; if more than one
/// matches, registration order wins. Provider-specific tokenizers register themselves before
/// the in-core default so they take precedence automatically.
/// </para>
/// <para>
/// The <see cref="Kind"/> property is stamped on emitted metrics as a <c>tokenizer_kind</c> tag,
/// so dashboards can distinguish authoritative numbers from approximations and filter or compare
/// across the two.
/// </para>
/// </remarks>
public interface ITokenizer
{
	/// <summary>
	/// Identifies this tokenizer in metric tags. Values like <c>"chars_per_token"</c>,
	/// <c>"tiktoken"</c>, <c>"anthropic_api"</c>. Stable, low-cardinality strings.
	/// </summary>
	string Kind { get; }

	/// <summary>
	/// True if this tokenizer can produce a count for requests targeting <paramref name="target"/>.
	/// The pipeline uses this to route per request — provider-specific tokenizers return true only
	/// for their own surfaces; the chars/4 fallback returns true for everything.
	/// </summary>
	bool SupportsTarget(TopHatTarget target);

	/// <summary>
	/// Counts tokens in <paramref name="body"/>. Async to support both local computation
	/// (returning <see cref="ValueTask{Int32}"/> synchronously) and API-backed tokenizers that
	/// require a network round-trip.
	/// </summary>
	/// <param name="body">UTF-8 encoded request body bytes.</param>
	/// <param name="target">The provider surface this request targets — lets implementations
	/// pick the right encoding (e.g., o200k vs cl100k for OpenAI).</param>
	/// <param name="model">The upstream model name as parsed from the request, when available.
	/// May be null if model detection failed; implementations should fall back to a sensible
	/// default for the target.</param>
	/// <param name="deferredEmit">
	/// Optional callback the implementation may invoke later with the actual count. Lets a
	/// tokenizer return a placeholder value synchronously (so the pipeline doesn't block on a
	/// network call) and emit the real count out-of-band when it's ready. The pipeline binds this
	/// callback to the appropriate metric counter with the correct tags. Sync implementations
	/// (chars/4, tiktoken) should ignore this and return the count directly.
	/// </param>
	/// <param name="cancellationToken">Cancellation for API-backed tokenizers.</param>
	ValueTask<int> CountTokensAsync(ReadOnlyMemory<byte> body, TopHatTarget target, string? model, Action<int>? deferredEmit, CancellationToken cancellationToken);
}
