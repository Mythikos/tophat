namespace TopHat.Providers;

/// <summary>
/// Coarse provider classification. Mirrors the host that the request is targeting; derived entirely
/// from <see cref="HttpRequestMessage.RequestUri"/>.
/// </summary>
public enum TopHatProviderKind
{
    Other,
    Anthropic,
    OpenAI,
}
