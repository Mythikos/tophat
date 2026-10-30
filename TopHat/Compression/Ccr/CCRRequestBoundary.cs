using System.Text.Json;
using System.Text.Json.Nodes;
using TopHat.Handlers;

namespace TopHat.Compression.CCR;

/// <summary>Enforces internal-history invariants at the actual outbound HTTP boundary.</summary>
internal static class CCRRequestBoundary
{
	/// <summary>Ensures CCR can inspect JSON history within the configured memory bound.</summary>
	public static async Task InspectAsync(HttpRequestMessage request, TopHatRequestContext context, long maximumBytes, CancellationToken cancellationToken)
	{
		var body = context.JsonBody as JsonObject ?? await ReadBodyAsync(request, maximumBytes, cancellationToken).ConfigureAwait(false);
		if (CCRRequestState.RestoreRetry(request, body, context.Target))
		{
			ReplaceContent(request, JsonSerializer.SerializeToUtf8Bytes(body));
		}
		if (body is not null)
		{
			context.JsonBody = body;
			context.RequestBodyBytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
			context.StreamingFromBody = body["stream"] is JsonValue stream && stream.TryGetValue<bool>(out var streaming) && streaming;
			if (body["model"] is JsonValue model && model.TryGetValue<string>(out var modelName))
			{
				context.Model = modelName;
			}
		}
	}

	/// <summary>Repairs the actual outgoing body after all transforms and returns orchestration eligibility.</summary>
	public static async Task<bool> FinalizeAsync(HttpRequestMessage request, TopHatRequestContext context, long maximumBytes, CancellationToken cancellationToken)
	{
		var body = await ReadBodyAsync(request, maximumBytes, cancellationToken).ConfigureAwait(false);
		if (context.CcrState is null)
		{
			return false;
		}
		if (body is null)
		{
			if (context.CcrState.HasCompressedPayloads)
			{
				throw new HttpRequestException("A later transform made a CCR-compressed request uninspectable; it cannot be forwarded safely.");
			}
			return false;
		}
		var ownsDefinition = context.CcrState.OwnsToolDefinition;
		if (ownsDefinition && CCRRequestPolicy.HasCallerToolCollision(body, context.Target, ownsDefinition))
		{
			throw new HttpRequestException("A later transform changed the injected CCR tool definition; it cannot be forwarded safely.");
		}
		var canRetrieve = CCRRequestPolicy.CanRetrieve(body, context.Target, ownsDefinition) && (!context.CcrState.HasCompressedPayloads || CCRRequestPolicy.HasInternalDefinition(body, context.Target, ownsDefinition));
		var changed = false;
		if (!canRetrieve)
		{
			changed |= context.CcrState.RestorePayloads(body, context.Target);
			changed |= CCRRequestPolicy.RemoveInternalDefinition(body, context.Target, context.CcrState.CreatedToolsProperty, ownsDefinition);
		}
		if (!CCRRequestPolicy.HasCallerToolCollision(body, context.Target, ownsDefinition))
		{
			changed |= CCRHistoryRepair.Repair(body, context.Target);
		}
		if (changed)
		{
			ReplaceContent(request, JsonSerializer.SerializeToUtf8Bytes(body));
		}
		context.JsonBody = body;
		context.StreamingFromBody = body["stream"] is JsonValue stream && stream.TryGetValue<bool>(out var streaming) && streaming;
		context.CcrState.PreserveForRetry(request, body, context.Target);
		return canRetrieve;
	}

	private static async Task<JsonObject?> ReadBodyAsync(HttpRequestMessage request, long maximumBytes, CancellationToken cancellationToken)
	{
		var content = request.Content;
		var mediaType = content?.Headers.ContentType?.MediaType;
		if (content is null || mediaType is null || !(string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
		{
			return null;
		}
		if (maximumBytes < 0 || content.Headers.ContentLength > maximumBytes)
		{
			throw InspectionLimitException();
		}
		// Unknown-length content is read once with a hard bound. Overflow fails before dispatch;
		// forwarding the partially consumed stream would be unsafe.
		if (content.Headers.ContentLength is null)
		{
			using (var buffer = new MemoryStream())
			{
				var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
				var chunk = new byte[8192];
				while (true)
				{
					var remaining = maximumBytes - buffer.Length;
					var readLength = remaining < chunk.Length ? (int)remaining + 1 : chunk.Length;
					var read = await input.ReadAsync(chunk.AsMemory(0, readLength), cancellationToken).ConfigureAwait(false);
					if (read == 0)
					{
						break;
					}
					if (buffer.Length + read > maximumBytes)
					{
						throw InspectionLimitException();
					}
					buffer.Write(chunk, 0, read);
				}
				ReplaceContent(request, buffer.ToArray());
				content = request.Content!;
			}
		}
		await content.LoadIntoBufferAsync(maximumBytes, cancellationToken).ConfigureAwait(false);
		var bytes = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			return JsonNode.Parse(bytes) as JsonObject;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static HttpRequestException InspectionLimitException() => new("CCR requires inspectable JSON history; the request exceeds MaxBodyInspectionBytes. Increase the configured inspection limit or explicitly bypass TopHat.");

	private static void ReplaceContent(HttpRequestMessage request, byte[] bytes)
	{
		var replacement = new ByteArrayContent(bytes);
		if (request.Content is not null)
		{
			foreach (var header in request.Content.Headers)
			{
				if (!string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
				{
					replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
				}
			}
			request.Content.Dispose();
		}
		request.Content = replacement;
	}
}
