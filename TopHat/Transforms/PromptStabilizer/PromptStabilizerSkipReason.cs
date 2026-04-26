namespace TopHat.Transforms.PromptStabilizer;

/// <summary>
/// Skip reasons surfaced as string tags on <c>tophat.transform.skipped{reason=...}</c>.
/// </summary>
internal enum PromptStabilizerSkipReason
{
    None,
    UnsupportedOpenAiModel,
    BelowThreshold,
    NoSystemOrInstructions,
    RestructureDisallowed,
    RegexTimeout,
    AlreadyStable,
    ResponsesInputShapeUnsupported,
}

internal static class PromptStabilizerSkipReasonExtensions
{
    public static string ToTag(this PromptStabilizerSkipReason reason) => reason switch
    {
        PromptStabilizerSkipReason.UnsupportedOpenAiModel => "unsupported_openai_model",
        PromptStabilizerSkipReason.BelowThreshold => "below_threshold",
        PromptStabilizerSkipReason.NoSystemOrInstructions => "no_system_or_instructions",
        PromptStabilizerSkipReason.RestructureDisallowed => "restructure_disallowed",
        PromptStabilizerSkipReason.RegexTimeout => "regex_timeout",
        PromptStabilizerSkipReason.AlreadyStable => "already_stable",
        PromptStabilizerSkipReason.ResponsesInputShapeUnsupported => "responses_input_shape_unsupported",
        _ => "unknown",
    };
}
