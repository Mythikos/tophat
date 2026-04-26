namespace TopHat.Tokenizers.Anthropic;

/// <summary>
/// Operating mode for <see cref="AnthropicCountTokensTokenizer"/>. Trades request-path latency
/// against per-request metric correlation.
/// </summary>
public enum AnthropicTokenizerMode
{
	/// <summary>
	/// Default. The tokenizer returns 0 synchronously and schedules an
	/// <c>https://api.anthropic.com/v1/messages/count_tokens</c> call in the background; when
	/// it completes, the metric counter is incremented out-of-band via the deferred-emit
	/// callback the pipeline provides. The request path is never blocked. The pre/post
	/// counters end up with correct cumulative sums; per-request reduction-ratio histograms
	/// land as 0 because the pipeline can't know the real values at request time.
	/// </summary>
	Deferred = 0,

	/// <summary>
	/// The tokenizer awaits the count_tokens HTTP call before returning the count. Adds
	/// network latency to every request (typically ~50-100ms). Pre/post counters AND the
	/// reduction-ratio histogram emit accurate per-request values. Use when accurate
	/// per-request correlation is more important than throughput.
	/// </summary>
	Blocking = 1,
}

/// <summary>
/// Options for <see cref="AnthropicCountTokensTokenizer"/>.
/// </summary>
public sealed class AnthropicTokenizerOptions
{
	/// <summary>
	/// Anthropic API key for authenticating <c>count_tokens</c> calls. Falls back to the
	/// <c>ANTHROPIC_API_KEY</c> environment variable when not set explicitly. Counting
	/// tokens via this endpoint is free (does not count against TPM and is not billed) but
	/// still requires authentication.
	/// </summary>
	public string? ApiKey { get; set; }

	/// <summary>Operating mode. Defaults to <see cref="AnthropicTokenizerMode.Deferred"/> — fire-and-forget so the request path isn't blocked.</summary>
	public AnthropicTokenizerMode Mode { get; set; } = AnthropicTokenizerMode.Deferred;

	/// <summary>
	/// Anthropic API version header value. Defaults to a known-stable version; override if
	/// a newer model version's count_tokens response shape changes.
	/// </summary>
	public string AnthropicVersion { get; set; } = "2023-06-01";

	/// <summary>
	/// Base URL for Anthropic's API. Override for proxies, regional endpoints, or test
	/// fakes. Defaults to <c>https://api.anthropic.com</c>.
	/// </summary>
	public string BaseUrl { get; set; } = "https://api.anthropic.com";
}
