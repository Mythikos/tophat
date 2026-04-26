using Microsoft.Extensions.Logging;
using TopHat.Providers;
using TopHat.Streaming;

namespace TopHat.Transforms;

/// <summary>
/// Logs a single Information line per response describing what the pipeline observed. Drop-in tool
/// for consumers who want to confirm response transforms are being invoked at all. Does not
/// mutate (nothing could, on the response side).
/// </summary>
public sealed partial class LoggingResponseTransform : IResponseTransform
{
    public ValueTask InvokeAsync(ResponseTransformContext context, CancellationToken cancellationToken)
    {
        LogInvocation(context.Logger, context.Target, context.Model, context.StatusCode, context.Mode, context.Body is not null, context.ObservedEventCount, context.LocalId);
        return ValueTask.CompletedTask;
    }

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "TopHat LoggingResponseTransform observed Target={Target}, Model={Model}, Status={StatusCode}, Mode={Mode}, BodyParsed={BodyParsed}, EventCount={EventCount} (LocalId={LocalId})")]
    private static partial void LogInvocation(ILogger logger, TopHatTarget target, string model, int statusCode, TeeMode mode, bool bodyParsed, int eventCount, string localId);
}
