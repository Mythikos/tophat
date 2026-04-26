using System.Text.RegularExpressions;

namespace TopHat.Transforms.CacheAligner;

/// <summary>
/// Options for <c>AnthropicCacheAlignerTransform</c>. All defaults favor safety: conservative
/// model allowlist, one breakpoint, dynamic extraction disabled, shape changes allowed (purely
/// additive for the common case).
/// </summary>
public sealed class AnthropicCacheAlignerOptions
{
    /// <summary>
    /// Allowlist of model patterns. Uniform convention: <c>claude-{family}[-{variant}]-*</c> with a
    /// trailing wildcard. Models not matching any entry are skipped — conservative default.
    /// </summary>
    public IList<string> AllowedModelPatterns { get; } = new List<string>
    {
        "claude-3-*",
        "claude-sonnet-4-*",
        "claude-opus-4-*",
        "claude-haiku-4-*",
    };

    /// <summary>
    /// When true, places a second cache breakpoint at end-of-tools in addition to end-of-system.
    /// Default false — Anthropic's prefix-cache covers tools+system in one slot from end-of-system
    /// alone, so a second breakpoint only helps when tools and system vary independently. Enabling
    /// pays the +25% write premium on the second cache slot.
    /// </summary>
    public bool CacheToolsIndependently { get; set; }

    /// <summary>
    /// Opt-in dynamic-content extraction (Mode B). Off by default. When true, detects ISO 8601
    /// dates and UUIDs (plus any <see cref="DynamicPatterns"/> the consumer adds) in the system
    /// prompt and moves them to the tail so the stable prefix is longer.
    /// </summary>
    public bool ExperimentalDynamicExtraction { get; set; }

    /// <summary>
    /// Additional regex patterns to detect dynamic content. Applied alongside the built-in
    /// ISO 8601 + UUID defaults when <see cref="ExperimentalDynamicExtraction"/> is true. Each
    /// pattern runs with <see cref="DynamicPatternTimeout"/> per request.
    /// </summary>
    public IList<Regex> DynamicPatterns { get; } = new List<Regex>();

    /// <summary>Per-regex match timeout. Default 50ms.</summary>
    public TimeSpan DynamicPatternTimeout { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Dynamic spans falling within the last <c>max(fraction × systemLength, minChars)</c>
    /// characters of the system prompt are left in place. Default 0.25 (last quarter).
    /// </summary>
    public double DynamicExtractionTailFraction { get; set; } = 0.25;

    /// <summary>Minimum-chars floor for the tail-fraction heuristic. Default 500.</summary>
    public int DynamicExtractionTailMinChars { get; set; } = 500;

    /// <summary>
    /// When false, the transform will NOT convert a string-form system prompt into a multi-block
    /// array. Applies to both Mode A and Mode B. Array-form system prompts and tools-only paths
    /// still work (no restructure needed). Default true.
    /// </summary>
    public bool AllowSystemRestructure { get; set; } = true;
}
