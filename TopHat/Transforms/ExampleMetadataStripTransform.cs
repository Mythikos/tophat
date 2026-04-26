using Microsoft.Extensions.Options;
using System.Text.Json.Nodes;

namespace TopHat.Transforms;

/// <summary>
/// Configuration for <see cref="ExampleMetadataStripTransform"/>. The single option is the
/// top-level field name to strip when present.
/// </summary>
public sealed class ExampleMetadataStripOptions
{
    public string FieldName { get; set; } = "metadata";
}

/// <summary>
/// Example transform that removes a configurable top-level field from the JSON request body.
/// Exists primarily to exercise the mutation + serialization path in tests and demonstrate the
/// <see cref="RequestTransformContext.MarkMutated"/> contract for new transform authors. Not
/// intended as a recommended production transform.
/// </summary>
public sealed class ExampleMetadataStripTransform : IRequestTransform
{
    private readonly string _fieldName;

    public ExampleMetadataStripTransform(IOptions<ExampleMetadataStripOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this._fieldName = options.Value.FieldName ?? "metadata";
    }

    public ValueTask InvokeAsync(RequestTransformContext context, CancellationToken cancellationToken)
    {
        if (context.Body is not JsonObject obj)
        {
            return ValueTask.CompletedTask;
        }

        if (!obj.ContainsKey(this._fieldName))
        {
            return ValueTask.CompletedTask;
        }

        obj.Remove(this._fieldName);
        context.MarkMutated();
        return ValueTask.CompletedTask;
    }
}
