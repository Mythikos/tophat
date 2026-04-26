using System.Collections.Concurrent;

namespace TopHat.Feedback;

/// <summary>
/// Default <see cref="ICompressionFeedbackStore"/> shipped in TopHat core. Process-lifetime
/// counters in a <see cref="ConcurrentDictionary{TKey, TValue}"/>. No persistence — data is
/// lost on restart. For cross-restart durability use <c>FileCompressionFeedbackStore</c>.
/// </summary>
/// <remarks>
/// Auto-registered by <c>AddTopHat()</c> so the recording layer is on out-of-the-box. The
/// decision layer that consults this data is opt-in via
/// <c>AddTopHatFeedbackDecisions()</c> — without it, this store accumulates statistics for
/// observability without changing compression behavior.
/// </remarks>
public class InMemoryCompressionFeedbackStore : ICompressionFeedbackStore
{
	// Per-tool mutable state. Internal so file-backed subclass can iterate and persist.
	internal sealed class ToolEntry
	{
		public long TotalCompressions;
		public long TotalRetrievals;
		public long FullRetrievals;
		public long SearchRetrievals;
		public long BudgetExhausted;
		public FeedbackOverride ManualOverride;
		public DateTimeOffset? LastEventUtc;
	}

	private readonly ConcurrentDictionary<string, ToolEntry> _entries = new(StringComparer.Ordinal);

	/// <summary>
	/// Provides subclasses access to the in-memory state for persistence purposes. Read-only
	/// from outside the assembly.
	/// </summary>
	internal IReadOnlyDictionary<string, ToolEntry> Entries => this._entries;

	/// <inheritdoc/>
	public void RecordCompression(string? toolName)
	{
		if (string.IsNullOrEmpty(toolName))
		{
			return;
		}

		var entry = this._entries.GetOrAdd(toolName, _ => new ToolEntry());
		Interlocked.Increment(ref entry.TotalCompressions);
		entry.LastEventUtc = DateTimeOffset.UtcNow;
		this.OnEntryMutated(toolName);
	}

	/// <inheritdoc/>
	public void RecordRetrieval(string? toolName, RetrievalKind kind)
	{
		if (string.IsNullOrEmpty(toolName))
		{
			return;
		}

		var entry = this._entries.GetOrAdd(toolName, _ => new ToolEntry());
		Interlocked.Increment(ref entry.TotalRetrievals);
		switch (kind)
		{
			case RetrievalKind.Full:
				Interlocked.Increment(ref entry.FullRetrievals);
				break;
			case RetrievalKind.Search:
				Interlocked.Increment(ref entry.SearchRetrievals);
				break;
			case RetrievalKind.BudgetExhausted:
				Interlocked.Increment(ref entry.BudgetExhausted);
				break;
		}
		entry.LastEventUtc = DateTimeOffset.UtcNow;
		this.OnEntryMutated(toolName);
	}

	/// <inheritdoc/>
	public virtual CompressionFeedbackStats? GetStats(string toolName)
	{
		ArgumentException.ThrowIfNullOrEmpty(toolName);

		if (!this._entries.TryGetValue(toolName, out var entry))
		{
			return null;
		}

		return new CompressionFeedbackStats(
			ToolName: toolName,
			TotalCompressions: Interlocked.Read(ref entry.TotalCompressions),
			TotalRetrievals: Interlocked.Read(ref entry.TotalRetrievals),
			FullRetrievals: Interlocked.Read(ref entry.FullRetrievals),
			SearchRetrievals: Interlocked.Read(ref entry.SearchRetrievals),
			BudgetExhausted: Interlocked.Read(ref entry.BudgetExhausted),
			ManualOverride: entry.ManualOverride,
			LastEventUtc: entry.LastEventUtc);
	}

	/// <inheritdoc/>
	public void SetManualOverride(string toolName, FeedbackOverride feedbackOverride)
	{
		ArgumentException.ThrowIfNullOrEmpty(toolName);

		var entry = this._entries.GetOrAdd(toolName, _ => new ToolEntry());
		entry.ManualOverride = feedbackOverride;
		this.OnEntryMutated(toolName);
	}

	/// <inheritdoc/>
	public void SeedStats(string toolName, long totalCompressions, long totalRetrievals, long fullRetrievals, long searchRetrievals, long budgetExhausted)
	{
		ArgumentException.ThrowIfNullOrEmpty(toolName);

		var entry = this._entries.GetOrAdd(toolName, _ => new ToolEntry());
		Interlocked.Add(ref entry.TotalCompressions, totalCompressions);
		Interlocked.Add(ref entry.TotalRetrievals, totalRetrievals);
		Interlocked.Add(ref entry.FullRetrievals, fullRetrievals);
		Interlocked.Add(ref entry.SearchRetrievals, searchRetrievals);
		Interlocked.Add(ref entry.BudgetExhausted, budgetExhausted);
		entry.LastEventUtc ??= DateTimeOffset.UtcNow;
		this.OnEntryMutated(toolName);
	}

	/// <summary>
	/// Hook for subclasses (e.g., file-backed) to react to state changes — schedule a flush,
	/// emit a metric, etc. Default no-op. Called after the in-memory state has been updated;
	/// must not throw.
	/// </summary>
	protected virtual void OnEntryMutated(string toolName)
	{
	}
}
