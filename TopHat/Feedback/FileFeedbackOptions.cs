namespace TopHat.Feedback;

/// <summary>
/// Configuration for <see cref="FileCompressionFeedbackStore"/>.
/// </summary>
public sealed class FileFeedbackOptions
{
	/// <summary>
	/// Path to the JSON file. Defaults to <c>{AppContext.BaseDirectory}/.tophat/feedback.json</c>.
	/// Override with an absolute path for centralized storage. The directory is created on first
	/// flush if it doesn't already exist.
	/// </summary>
	public string Path { get; set; } = System.IO.Path.Combine(AppContext.BaseDirectory, ".tophat", "feedback.json");

	/// <summary>
	/// Maximum delay between when a record event mutates state and when the file is flushed.
	/// Defaults to 5 seconds. Smaller values reduce data loss on crash; larger values reduce
	/// I/O frequency. The flush also fires on store disposal regardless.
	/// </summary>
	public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(5);

	/// <summary>
	/// Number of pending mutations that triggers an immediate flush regardless of
	/// <see cref="FlushInterval"/>. Prevents unbounded buffering on bursty workloads.
	/// Defaults to 100.
	/// </summary>
	public int FlushOnPendingCount { get; set; } = 100;
}
