using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TopHat.Tokenizers;

namespace TopHat.Tokenizers.Anthropic.DependencyInjection;

/// <summary>
/// Extension methods for registering <see cref="AnthropicCountTokensTokenizer"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
	/// <summary>
	/// Adds <see cref="AnthropicCountTokensTokenizer"/> to the <see cref="ITokenizer"/> pool.
	/// Provides bit-exact token counts for Anthropic Messages requests via the
	/// <c>/v1/messages/count_tokens</c> endpoint. Counting via this endpoint is free (does not
	/// count against TPM and is not billed) but requires a network round-trip per count call.
	/// </summary>
	/// <remarks>
	/// <para>
	/// V1 is blocking: each request awaits the count_tokens HTTP call before returning,
	/// adding network latency (~50-100ms) on the request path. Use only when accurate token
	/// correlation matters more than throughput; otherwise rely on the in-core chars/4 default.
	/// </para>
	/// <para>
	/// API key resolution: explicit <c>options.ApiKey</c> takes precedence; otherwise falls
	/// back to the <c>ANTHROPIC_API_KEY</c> environment variable. If neither is present,
	/// counting is silently skipped (returns 0).
	/// </para>
	/// </remarks>
	public static IServiceCollection AddTopHatAnthropicTokenizer(this IServiceCollection services, Action<AnthropicTokenizerOptions>? configure = null)
	{
		ArgumentNullException.ThrowIfNull(services);

		if (configure is not null)
		{
			services.Configure(configure);
		}
		else
		{
			services.AddOptions<AnthropicTokenizerOptions>();
		}

		// Use a typed HttpClient so the tokenizer plays nicely with HttpClientFactory's
		// connection pooling and handler-lifetime management. AddHttpClient registers the
		// concrete type as transient and binds an HttpClient into its constructor.
		services.AddHttpClient<AnthropicCountTokensTokenizer>();

		// TryAddEnumerable requires a distinguishable implementation type — factory-style
		// registrations (sp => ...) collapse to ImplementationType=null and collide with the
		// chars/4 default's ITokenizer registration. The typed overload below carries
		// AnthropicCountTokensTokenizer as the implementation type, which is distinct from
		// CharsPerTokenTokenizer, so registration succeeds. Resolution as ITokenizer goes
		// through the AddHttpClient-registered constructor and returns a singleton-scoped
		// instance per the descriptor lifetime.
		services.TryAddEnumerable(ServiceDescriptor.Singleton<ITokenizer, AnthropicCountTokensTokenizer>());

		return services;
	}
}
