using System.Text.RegularExpressions;

namespace TopHat.Transforms.PromptStabilizer;

/// <summary>
/// Options for <c>OpenAiPromptStabilizerTransform</c>. Defaults favor safety: broad but
/// well-defined model allowlist, dynamic extraction off, shape changes allowed.
/// </summary>
/// <remarks>
/// OpenAI's prompt caching activates automatically when the first ≥1024 tokens of the prompt are
/// byte-identical to a recent request. This transform's job is <b>structural stabilization</b>:
/// keep the stable prefix first, and — when <see cref="ExperimentalDynamicExtraction"/> is on —
/// move ISO 8601 dates / UUIDs / user-supplied volatile patterns out of the prefix so it stays
/// byte-stable across calls. The transform never adds <c>cache_control</c> markers; OpenAI has no
/// such concept.
/// </remarks>
public sealed class OpenAiPromptStabilizerOptions
{
    /// <summary>
    /// Allowlist of OpenAI model glob patterns. Per OpenAI docs, prompt caching is enabled for "all
    /// recent models, gpt-4o and newer". Conservative default — models not matching any entry are
    /// skipped. Pattern language: <c>*</c> matches <c>[-._a-zA-Z0-9]*</c>.
    /// </summary>
    public IList<string> AllowedModelPatterns { get; } = new List<string>
    {
        "gpt-4o",
        "gpt-4o-*",
        "gpt-4.1",
        "gpt-4.1-*",
        "o1",
        "o1-*",
        "o3",
        "o3-*",
        "o4",
        "o4-*",
        "gpt-5",
        "gpt-5-*",
    };

    /// <summary>
    /// Minimum prompt prefix size (in tokens) below which the transform skips. OpenAI's prefix
    /// cache fires at 1024 tokens. The char-based proxy is 4× this value.
    /// </summary>
    public int MinimumTokens { get; set; } = 1024;

    /// <summary>
    /// Opt-in dynamic-content extraction. Off by default. When true, detects ISO 8601 dates and
    /// UUIDs (plus any <see cref="DynamicPatterns"/> the consumer adds) in the system / instructions
    /// text and moves them to the tail so the stable prefix is longer.
    /// </summary>
    public bool ExperimentalDynamicExtraction { get; set; }

    /// <summary>
    /// Additional regex patterns to detect dynamic content. Applied alongside built-in ISO 8601 +
    /// UUID defaults when <see cref="ExperimentalDynamicExtraction"/> is true. Each runs with
    /// <see cref="DynamicPatternTimeout"/>.
    /// </summary>
    public IList<Regex> DynamicPatterns { get; } = new List<Regex>();

    /// <summary>Per-regex match timeout. Default 50ms.</summary>
    public TimeSpan DynamicPatternTimeout { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Dynamic spans falling within the last <c>max(fraction × length, minChars)</c> characters of
    /// the stable text are left in place. Default 0.25 (last quarter).
    /// </summary>
    public double DynamicExtractionTailFraction { get; set; } = 0.25;

    /// <summary>Minimum-chars floor for the tail-fraction heuristic. Default 500.</summary>
    public int DynamicExtractionTailMinChars { get; set; } = 500;

    /// <summary>
    /// When false, the transform will NOT mutate the request shape (e.g. split a string system
    /// prompt into multiple message parts). Default true.
    /// </summary>
    public bool AllowRestructure { get; set; } = true;
}
