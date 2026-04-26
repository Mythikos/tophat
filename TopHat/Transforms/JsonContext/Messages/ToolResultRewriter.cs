namespace TopHat.Transforms.JsonContext.Messages;

/// <summary>
/// Replaces the string value of a located tool-result slot with compressed content.
/// </summary>
internal static class ToolResultRewriter
{
	/// <summary>
	/// Writes <paramref name="compressedText"/> into <paramref name="toolResult"/>'s owner node.
	/// The caller is responsible for calling <c>context.MarkMutated()</c> after all rewrites.
	/// </summary>
	public static void Rewrite(ToolResultRef toolResult, string compressedText)
	{
		toolResult.Owner[toolResult.Key] = compressedText;
	}
}
