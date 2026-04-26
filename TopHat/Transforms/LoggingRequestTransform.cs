using Microsoft.Extensions.Logging;

namespace TopHat.Transforms;

/// <summary>
/// Logs a single Information line per request describing what the pipeline saw. Drop-in tool for
/// consumers who want to confirm transforms are being invoked at all. Does not mutate.
/// </summary>
public sealed partial class LoggingRequestTransform : IRequestTransform
{
    public ValueTask InvokeAsync(RequestTransformContext context, CancellationToken cancellationToken)
    {
        LogInvocation(context.Logger, context.Target, context.Model, context.StreamingFromBody, context.Body is not null, context.LocalId);
        return ValueTask.CompletedTask;
    }

    [LoggerMessage(EventId = 2000, Level = LogLevel.Information, Message = "TopHat LoggingRequestTransform observed Target={Target}, Model={Model}, Streaming={Streaming}, BodyParsed={BodyParsed} (LocalId={LocalId})")]
    private static partial void LogInvocation(ILogger logger, Providers.TopHatTarget target, string model, bool streaming, bool bodyParsed, string localId);
}
