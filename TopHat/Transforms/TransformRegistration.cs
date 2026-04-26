using Microsoft.Extensions.Options;

namespace TopHat.Transforms;

internal enum TransformKind
{
    Request,
    RawRequest,
    Response,
}

/// <summary>
/// Declarative entry captured at service-registration time. The registry materializes these into
/// <see cref="TransformRegistration"/> instances when first resolved from DI.
/// </summary>
internal sealed class TransformRegistrationEntry
{
    public required TransformKind Kind { get; init; }

    public required Type TransformType { get; init; }

    public required int Order { get; init; }

    public required TransformFailureMode FailureMode { get; init; }

    public Func<RequestTransformContext, bool>? RequestFilter { get; init; }

    public Func<ResponseTransformContext, bool>? ResponseFilter { get; init; }
}

internal sealed class TransformRegistration
{
    public required TransformKind Kind { get; init; }

    public required Type TransformType { get; init; }

    public required int Order { get; init; }

    public required int RegistrationIndex { get; init; }

    public required TransformFailureMode FailureMode { get; init; }

    public Func<RequestTransformContext, bool>? RequestFilter { get; init; }

    public Func<ResponseTransformContext, bool>? ResponseFilter { get; init; }

    public string TransformName => this.TransformType.Name;
}

/// <summary>
/// Options shape used to accumulate transform registrations across multiple
/// <c>AddTopHat*Transform</c> calls.
/// </summary>
internal sealed class TopHatTransformOptions
{
    public List<TransformRegistrationEntry> Registrations { get; } = new();
}

/// <summary>
/// DI-resolved registry of all transform registrations (request-side and response-side). Materialized
/// once from the accumulated <see cref="TopHatTransformOptions"/> at first resolution.
/// </summary>
internal sealed class TopHatTransformRegistry
{
    private readonly List<TransformRegistration> _registrations = new();

    public TopHatTransformRegistry(IOptions<TopHatTransformOptions> options)
    {
        var entries = options.Value.Registrations;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            this._registrations.Add(new TransformRegistration
            {
                Kind = entry.Kind,
                TransformType = entry.TransformType,
                Order = entry.Order,
                RegistrationIndex = i,
                FailureMode = entry.FailureMode,
                RequestFilter = entry.RequestFilter,
                ResponseFilter = entry.ResponseFilter,
            });
        }
    }

    public IReadOnlyList<TransformRegistration> Registrations => this._registrations;
}
