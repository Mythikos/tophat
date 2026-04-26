using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TopHat.Configuration;
using TopHat.Feedback;
using TopHat.Handlers;
using TopHat.Providers;
using TopHat.Relevance;
using TopHat.Tokenizers;
using TopHat.Transforms;
using TopHat.Transforms.CacheAligner;
using TopHat.Transforms.JsonContext;
using TopHat.Transforms.JsonContext.Summarization;
using TopHat.Transforms.PromptStabilizer;

namespace TopHat.DependencyInjection;

/// <summary>
/// Extension methods for registering TopHat into an <see cref="IServiceCollection"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers TopHat's options, delegating handler, and the transform registry. Consumers wire
    /// the handler into their own named/typed <see cref="HttpClient"/> via
    /// <c>.AddHttpClient("name").AddHttpMessageHandler&lt;TopHatHandler&gt;()</c>.
    /// </summary>
    public static IServiceCollection AddTopHat(this IServiceCollection services, Action<TopHatOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is not null)
        {
            services.Configure(configure);
        }
        else
        {
            services.AddOptions<TopHatOptions>();
        }

        services.AddOptions<TopHatTransformOptions>();
        services.TryAddSingleton<TopHatTransformRegistry>();
        services.AddTransient<TopHatHandler>();

        // Register the chars/4 fallback tokenizer. Provider-specific tokenizers
        // (TopHat.Tokenizers.OpenAi, TopHat.Tokenizers.Anthropic) register themselves
        // via TryAddEnumerable as well, and the pipeline picks the most specific match
        // per request — so this default only fires for targets nothing else handles.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITokenizer, CharsPerTokenTokenizer>());

        // Register the in-memory feedback store as the default. Recording is on out-of-the-box
        // for observability — it's purely a counter increment per event, no behavior change
        // unless UseTopHatFeedbackDecisions() is also called. Users can swap to file-backed
        // (UseTopHatFileFeedbackStore) or no-op (UseTopHatNoopFeedbackStore) recording.
        services.TryAddSingleton<ICompressionFeedbackStore, InMemoryCompressionFeedbackStore>();

        return services;
    }

    /// <summary>
    /// Declares per-tool feedback overrides at startup. Lets users statically opt specific
    /// tools out of compression (or force compression) without enabling the empirical
    /// learning layer. Overrides apply unconditionally — independent of
    /// <c>UseTopHatFeedbackDecisions()</c>.
    /// </summary>
    /// <remarks>
    /// Precedence: store-based runtime overrides (via <c>SetManualOverride</c>) win when set;
    /// this config fills in when the store has no explicit override for a tool.
    /// </remarks>
    public static IServiceCollection UseTopHatFeedbackOverrides(this IServiceCollection services, Action<FeedbackOverridesConfiguration> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        return services;
    }

    /// <summary>
    /// Activates the feedback decision layer. Without this call, the feedback store accumulates
    /// stats for observability but doesn't change compression behavior. With it, the compressor
    /// consults stats per tool per request and may skip compression entirely when the empirical
    /// signal indicates the tool's outputs are typically needed in full. Thresholds are tunable
    /// via the configure callback; defaults match headroom's proven values.
    /// </summary>
    public static IServiceCollection UseTopHatFeedbackDecisions(this IServiceCollection services, Action<FeedbackThresholds>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Configure the thresholds; always set Enabled = true so the compressor can distinguish
        // "user opted in" from "DI auto-provided a default IOptions<T> instance." The user's
        // optional configure runs first so they can't accidentally clear the flag.
        services.Configure<FeedbackThresholds>(opt =>
        {
            configure?.Invoke(opt);
            opt.Enabled = true;
        });

        return services;
    }

    /// <summary>
    /// Replaces the default in-memory feedback store with a no-op store that drops all
    /// recordings. Use when feedback recording is explicitly unwanted (privacy, perf paranoia,
    /// isolated test environments).
    /// </summary>
    public static IServiceCollection UseTopHatNoopFeedbackStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Replace(ServiceDescriptor.Singleton<ICompressionFeedbackStore, NullCompressionFeedbackStore>());
        return services;
    }

    /// <summary>
    /// Replaces the default in-memory feedback store with a file-backed store. Stats persist
    /// across process restarts via JSON file at <see cref="FileFeedbackOptions.Path"/>
    /// (defaults to <c>{AppContext.BaseDirectory}/.tophat/feedback.json</c>). Lazy load on
    /// first read, batched async flush, atomic write-and-rename.
    /// </summary>
    public static IServiceCollection UseTopHatFileFeedbackStore(this IServiceCollection services, Action<FileFeedbackOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is not null)
        {
            services.Configure(configure);
        }
        else
        {
            services.AddOptions<FileFeedbackOptions>();
        }

        services.Replace(ServiceDescriptor.Singleton<ICompressionFeedbackStore, FileCompressionFeedbackStore>());
        return services;
    }

    /// <summary>
    /// Registers an <see cref="IRequestTransform"/> implementation to run in the request-side
    /// pipeline. Transforms are constructed per-request (transient). Configure ordering, filtering,
    /// and failure mode via the optional <paramref name="configure"/> callback.
    /// </summary>
    public static IServiceCollection AddTopHatRequestTransform<TTransform>(this IServiceCollection services, Action<RequestTransformRegistrationOptions>? configure = null) where TTransform : class, IRequestTransform
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<TopHatTransformOptions>();
        services.TryAddSingleton<TopHatTransformRegistry>();
        services.AddTransient<TTransform>();

        var options = new RequestTransformRegistrationOptions();
        configure?.Invoke(options);
        EnsureFailureModeSupported(typeof(TTransform), options.FailureMode);

        var entry = new TransformRegistrationEntry
        {
            Kind = TransformKind.Request,
            TransformType = typeof(TTransform),
            Order = options.Order,
            FailureMode = options.FailureMode,
            RequestFilter = options.Filter,
        };
        services.Configure<TopHatTransformOptions>(o => o.Registrations.Add(entry));

        return services;
    }

    /// <summary>
    /// Registers an <see cref="IRawRequestTransform"/> implementation. Same semantics as
    /// <see cref="AddTopHatRequestTransform{TTransform}"/> but the transform receives the full
    /// <see cref="HttpRequestMessage"/>.
    /// </summary>
    public static IServiceCollection AddTopHatRawRequestTransform<TTransform>(this IServiceCollection services, Action<RequestTransformRegistrationOptions>? configure = null) where TTransform : class, IRawRequestTransform
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<TopHatTransformOptions>();
        services.TryAddSingleton<TopHatTransformRegistry>();
        services.AddTransient<TTransform>();

        var options = new RequestTransformRegistrationOptions();
        configure?.Invoke(options);
        EnsureFailureModeSupported(typeof(TTransform), options.FailureMode);

        var entry = new TransformRegistrationEntry
        {
            Kind = TransformKind.RawRequest,
            TransformType = typeof(TTransform),
            Order = options.Order,
            FailureMode = options.FailureMode,
            RequestFilter = null,  // raw transforms don't get the parsed-body context
        };
        services.Configure<TopHatTransformOptions>(o => o.Registrations.Add(entry));

        return services;
    }

    /// <summary>
    /// Registers an <see cref="IResponseTransform"/> implementation to run in the response-side
    /// pipeline (observation-only). Transforms are constructed per-response (transient). Configure
    /// ordering, filtering, and failure mode via the optional <paramref name="configure"/>
    /// callback.
    /// </summary>
    /// <remarks>
    /// <b>Dispatch timing</b>: response transforms fire ONCE per response, inside the tee's async
    /// finalization path (EOF or async disposal). If the response stream is synchronously disposed
    /// (never walked via <c>ReadAsync</c> / <c>DisposeAsync</c>), response transforms DO NOT fire.
    /// Callers who register response transforms MUST use <c>await using</c> or await one of the
    /// content-read methods on the response.
    /// </remarks>
    public static IServiceCollection AddTopHatResponseTransform<TTransform>(this IServiceCollection services, Action<ResponseTransformRegistrationOptions>? configure = null) where TTransform : class, IResponseTransform
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<TopHatTransformOptions>();
        services.TryAddSingleton<TopHatTransformRegistry>();
        services.AddTransient<TTransform>();

        var options = new ResponseTransformRegistrationOptions();
        configure?.Invoke(options);
        EnsureFailureModeSupported(typeof(TTransform), options.FailureMode);

        var entry = new TransformRegistrationEntry
        {
            Kind = TransformKind.Response,
            TransformType = typeof(TTransform),
            Order = options.Order,
            FailureMode = options.FailureMode,
            ResponseFilter = options.Filter,
        };
        services.Configure<TopHatTransformOptions>(o => o.Registrations.Add(entry));

        return services;
    }

    /// <summary>
    /// Convenience registration for <see cref="AnthropicCacheAlignerTransform"/>. Pre-applies the
    /// Anthropic target filter and a conventional Order of 100. Consumers needing finer control
    /// skip this and call <see cref="AddTopHatRequestTransform{TTransform}"/> directly.
    /// </summary>
    public static IServiceCollection AddTopHatAnthropicCacheAligner(this IServiceCollection services, Action<AnthropicCacheAlignerOptions>? configureOptions = null, Action<RequestTransformRegistrationOptions>? configureRegistration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configureOptions is not null)
        {
            services.Configure(configureOptions);
        }
        else
        {
            services.AddOptions<AnthropicCacheAlignerOptions>();
        }

        return services.AddTopHatRequestTransform<AnthropicCacheAlignerTransform>(cfg =>
        {
            cfg.AppliesTo(TopHatTarget.AnthropicMessages);
            cfg.WithOrder(100);
            configureRegistration?.Invoke(cfg);
        });
    }

    /// <summary>
    /// Convenience registration for <see cref="OpenAiPromptStabilizerTransform"/>. Pre-applies the
    /// OpenAI target filter (ChatCompletions + Responses) and a conventional Order of 100.
    /// Consumers needing finer control skip this and call
    /// <see cref="AddTopHatRequestTransform{TTransform}"/> directly.
    /// </summary>
    public static IServiceCollection AddTopHatOpenAiPromptStabilizer(this IServiceCollection services, Action<OpenAiPromptStabilizerOptions>? configureOptions = null, Action<RequestTransformRegistrationOptions>? configureRegistration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configureOptions is not null)
        {
            services.Configure(configureOptions);
        }
        else
        {
            services.AddOptions<OpenAiPromptStabilizerOptions>();
        }

        return services.AddTopHatRequestTransform<OpenAiPromptStabilizerTransform>(cfg =>
        {
            cfg.AppliesTo(TopHatTarget.OpenAIChatCompletions, TopHatTarget.OpenAIResponses);
            cfg.WithOrder(100);
            configureRegistration?.Invoke(cfg);
        });
    }

    /// <summary>
    /// Convenience registration for <see cref="JsonContextCompressorTransform"/>. Pre-applies
    /// filters for all supported targets (Anthropic Messages, OpenAI ChatCompletions, OpenAI Responses)
    /// and a conventional <c>Order = 200</c> so it runs after the cache aligner and prompt stabilizer.
    /// Consumers needing finer control skip this and call
    /// <see cref="AddTopHatRequestTransform{TTransform}"/> directly.
    /// </summary>
    /// <remarks>
    /// <para>Requires at least one <see cref="IRelevanceScorer"/> to be registered separately —
    /// call <c>AddTopHatBm25Relevance()</c> (from the <c>TopHat.Relevance.BM25</c> package) for keyword scoring, or
    /// <c>AddTopHatOnnxRelevance(...)</c> from the <c>TopHat.Relevance.Onnx</c> package for
    /// semantic scoring. Both extensions can be called together; when more than one scorer is
    /// present in DI, they are automatically fused via normalized-sum fusion (see
    /// <see cref="FusedRelevanceScorer"/>).</para>
    /// <para>Resolving the transform with zero scorers registered throws a clear setup error
    /// at request time rather than silently falling back to a default.</para>
    /// </remarks>
    public static IServiceCollection AddTopHatJsonContextCompressor(this IServiceCollection services, Action<JsonContextCompressorOptions>? configureOptions = null, Action<RequestTransformRegistrationOptions>? configureRegistration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configureOptions is not null)
        {
            services.Configure(configureOptions);
        }
        else
        {
            services.AddOptions<JsonContextCompressorOptions>();
        }

        // Register default dropped-item summarizers. Consumers wanting different behavior can
        // re-Configure the options or register additional IDroppedItemsSummarizer implementations
        // before or after this call; TryAddEnumerable is idempotent per implementation type.
        services.AddOptions<CategoricalSummarizerOptions>();
        services.AddOptions<NumericFieldSummarizerOptions>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDroppedItemsSummarizer, CategoricalSummarizer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDroppedItemsSummarizer, NumericFieldSummarizer>());

        return services.AddTopHatRequestTransform<JsonContextCompressorTransform>(cfg =>
        {
            cfg.AppliesTo(TopHatTarget.AnthropicMessages, TopHatTarget.OpenAIChatCompletions, TopHatTarget.OpenAIResponses);
            cfg.WithOrder(200);
            configureRegistration?.Invoke(cfg);
        });
    }

    private static void EnsureFailureModeSupported(Type transformType, TransformFailureMode mode)
    {
        if (mode == TransformFailureMode.CircuitBreaker)
        {
            throw new NotImplementedException(
                $"TransformFailureMode.CircuitBreaker is not implemented in M3. Transform '{transformType.Name}' " +
                $"cannot use it. Use FailOpen (default) or FailClosed instead.");
        }
    }
}
