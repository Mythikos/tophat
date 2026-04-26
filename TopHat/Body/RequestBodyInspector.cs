using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Nodes;
using TopHat.Configuration;
using TopHat.Diagnostics;
using TopHat.Handlers;

namespace TopHat.Body;

/// <summary>
/// Inspects a JSON request body to extract <c>stream</c> and <c>model</c> for metric tagging, and
/// parses the full tree into a <see cref="JsonNode"/> so transforms can mutate it. Uses
/// <see cref="HttpContent.LoadIntoBufferAsync(long, CancellationToken)"/> to buffer in place so the
/// upstream send reads the same bytes without any content mutation.
/// </summary>
/// <remarks>
/// Inspection is <b>Content-Length-gated</b>: only runs when the length is known and within cap.
/// This avoids the corrupted-content risk of <c>LoadIntoBufferAsync</c> throwing partway through
/// a chunked/unknown-length body.
/// </remarks>
internal sealed class RequestBodyInspector
{
    private readonly IOptions<TopHatOptions> _options;
    private readonly ILogger _logger;

    public RequestBodyInspector(IOptions<TopHatOptions> options, ILogger logger)
    {
        this._options = options;
        this._logger = logger;
    }

    public async Task InspectAsync(HttpRequestMessage request, TopHatRequestContext context, CancellationToken cancellationToken)
    {
        var content = request.Content;
        if (content is null)
        {
            TopHatLogEvents.BodyInspectionSkipped(this._logger, "null_body", context.LocalId);
            return;
        }

        var mediaType = content.Headers.ContentType?.MediaType;
        if (!IsJsonMediaType(mediaType))
        {
            TopHatLogEvents.BodyInspectionSkipped(this._logger, "non_json", context.LocalId);
            return;
        }

        var length = content.Headers.ContentLength;
        if (length is null)
        {
            TopHatLogEvents.BodyInspectionSkipped(this._logger, "unknown_length", context.LocalId);
            return;
        }

        var cap = this._options.Value.MaxBodyInspectionBytes;
        if (length > cap)
        {
            TopHatLogEvents.BodyInspectionSkipped(this._logger, "over_cap", context.LocalId);
            return;
        }

        // Content-Length known and ≤ cap — LoadIntoBufferAsync will not overflow.
        await content.LoadIntoBufferAsync(cap, cancellationToken).ConfigureAwait(false);

        byte[] bytes;
        try
        {
            bytes = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            TopHatLogEvents.BodyInspectionSkipped(this._logger, "read_failed", context.LocalId);
            return;
        }

        if (!TryExtractStreamAndModel(bytes, out var streaming, out var model))
        {
            TopHatLogEvents.BodyInspectionSkipped(this._logger, "parse_failed", context.LocalId);
            return;
        }

        context.StreamingFromBody = streaming;
        if (!string.IsNullOrEmpty(model))
        {
            context.Model = model;
        }

        // Parse the full tree so transforms can mutate it. Bytes snapshot persists for
        // fail-open rollback in the transform pipeline.
        try
        {
            context.JsonBody = JsonNode.Parse(bytes);
            context.RequestBodyBytes = bytes;
        }
        catch (JsonException)
        {
            // Metadata extraction succeeded but full tree parse failed — leave JsonBody null so
            // transforms no-op; we already have the stream/model values we need for metrics.
            TopHatLogEvents.BodyInspectionSkipped(this._logger, "tree_parse_failed", context.LocalId);
        }
    }

    private static bool IsJsonMediaType(string? mediaType)
    {
        if (string.IsNullOrEmpty(mediaType))
        {
            return false;
        }

        if (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // application/*+json family (vnd.api+json, problem+json, etc.)
        return mediaType.StartsWith("application/", StringComparison.OrdinalIgnoreCase) &&
            mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Scans the JSON body for the two top-level fields we care about, short-circuiting once both
    /// are seen. Returns false only if the JSON is syntactically malformed; missing fields return
    /// true with default values.
    /// </summary>
    private static bool TryExtractStreamAndModel(ReadOnlySpan<byte> json, out bool streaming, out string? model)
    {
        streaming = false;
        model = null;

        try
        {
            var reader = new Utf8JsonReader(json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            var depth = 1;
            var foundStream = false;
            var foundModel = false;

            while (reader.Read() && depth > 0)
            {
                if (reader.TokenType == JsonTokenType.StartObject || reader.TokenType == JsonTokenType.StartArray)
                {
                    depth++;
                    reader.Skip();
                    depth--;
                    continue;
                }

                if (reader.TokenType == JsonTokenType.EndObject || reader.TokenType == JsonTokenType.EndArray)
                {
                    depth--;
                    continue;
                }

                if (depth != 1 || reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                if (!foundStream && reader.ValueTextEquals("stream"u8))
                {
                    if (!reader.Read())
                    {
                        return false;
                    }

                    if (reader.TokenType == JsonTokenType.True)
                    {
                        streaming = true;
                    }

                    foundStream = true;
                }
                else if (!foundModel && reader.ValueTextEquals("model"u8))
                {
                    if (!reader.Read())
                    {
                        return false;
                    }

                    if (reader.TokenType == JsonTokenType.String)
                    {
                        model = reader.GetString();
                    }

                    foundModel = true;
                }
                else
                {
                    if (!reader.Read())
                    {
                        return false;
                    }

                    if (reader.TokenType == JsonTokenType.StartObject || reader.TokenType == JsonTokenType.StartArray)
                    {
                        reader.Skip();
                    }
                }

                if (foundStream && foundModel)
                {
                    return true;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
