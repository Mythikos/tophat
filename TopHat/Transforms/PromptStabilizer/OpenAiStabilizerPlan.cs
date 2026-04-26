namespace TopHat.Transforms.PromptStabilizer;

internal enum OpenAiStabilizerDecision
{
    ApplyChat,
    ApplyResponses,
    Skip,
}

/// <summary>
/// Output of <see cref="OpenAiStabilizerPlanner"/>. Carries the decision plus the measured stable
/// prefix size (for debug logging) and, on skip, the reason.
/// </summary>
internal readonly record struct OpenAiStabilizerPlan(
    OpenAiStabilizerDecision Decision,
    PromptStabilizerSkipReason SkipReason,
    int PrefixChars)
{
    public static OpenAiStabilizerPlan Apply(OpenAiStabilizerDecision decision, int prefixChars) =>
        new(decision, PromptStabilizerSkipReason.None, prefixChars);

    public static OpenAiStabilizerPlan Skip(PromptStabilizerSkipReason reason, int prefixChars = 0) =>
        new(OpenAiStabilizerDecision.Skip, reason, prefixChars);

    public bool IsSkip => this.Decision == OpenAiStabilizerDecision.Skip;
}
