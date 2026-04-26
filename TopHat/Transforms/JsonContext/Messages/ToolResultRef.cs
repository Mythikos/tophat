using System.Text.Json.Nodes;

namespace TopHat.Transforms.JsonContext.Messages;

/// <summary>
/// Points to a single tool-result string in a request body. Returned by
/// <see cref="ToolResultLocator"/> and consumed by <see cref="ToolResultRewriter"/>.
/// </summary>
internal sealed class ToolResultRef
{
	/// <summary>The <see cref="JsonObject"/> that owns <see cref="Key"/>.</summary>
	public required JsonObject Owner { get; init; }

	/// <summary>The property name whose value is the tool-result string ("content", "output", or "text").</summary>
	public required string Key { get; init; }

	/// <summary>The current string value — read once at locate time, not re-read on rewrite.</summary>
	public required string Text { get; init; }
}
