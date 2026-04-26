namespace TopHat.Transforms.JsonContext.Summarization;

/// <summary>
/// Context passed to an <see cref="IDroppedItemsSummarizer"/> describing the compression event.
/// </summary>
public sealed class SummarizationContext
{
	/// <summary>Total number of items in the original array (before compression).</summary>
	public required int OriginalCount { get; init; }

	/// <summary>Number of items retained by compression.</summary>
	public required int KeptCount { get; init; }

	/// <summary>Number of items dropped by compression. Equals <c>OriginalCount - KeptCount</c>.</summary>
	public int DroppedCount => this.OriginalCount - this.KeptCount;
}
