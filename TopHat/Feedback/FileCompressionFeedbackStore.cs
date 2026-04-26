using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TopHat.Feedback;

/// <summary>
/// File-backed <see cref="ICompressionFeedbackStore"/>. Wraps the in-memory store with JSON
/// persistence: lazy load on first access, batched async flush, atomic write-and-rename.
/// Survives process restart so feedback accumulates across deployments.
/// </summary>
/// <remarks>
/// <para>
/// Single-writer assumption: one process per file. Multi-instance deployments writing to a
/// shared file would race; for that case, use a dedicated external store (future
/// <c>TopHat.Feedback.Sqlite</c> / <c>TopHat.Feedback.Redis</c>).
/// </para>
/// <para>
/// On crash, at most <see cref="FileFeedbackOptions.FlushInterval"/> worth of unflushed events
/// are lost. Feedback data is statistical, not transactional — this is the right trade-off
/// against fsync-per-event overhead.
/// </para>
/// </remarks>
public sealed partial class FileCompressionFeedbackStore : InMemoryCompressionFeedbackStore, IDisposable, IAsyncDisposable
{
	private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

	private readonly FileFeedbackOptions _options;
	private readonly ILogger<FileCompressionFeedbackStore> _logger;
	private readonly Lock _flushLock = new();
	private readonly Timer _flushTimer;
	private readonly CancellationTokenSource _disposalCts = new();

	private int _pendingMutations;
	private bool _loaded;
	private bool _disposed;

	public FileCompressionFeedbackStore(IOptions<FileFeedbackOptions> options, ILogger<FileCompressionFeedbackStore> logger)
	{
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(logger);
		this._options = options.Value;
		this._logger = logger;
		this._flushTimer = new Timer(this.OnFlushTimerTick, state: null, this._options.FlushInterval, this._options.FlushInterval);
	}

	/// <inheritdoc/>
	public override CompressionFeedbackStats? GetStats(string toolName) => this.WithLoaded(() => base.GetStats(toolName));

	private CompressionFeedbackStats? WithLoaded(Func<CompressionFeedbackStats?> action)
	{
		this.EnsureLoaded();
		return action();
	}

	/// <inheritdoc/>
	protected override void OnEntryMutated(string toolName)
	{
		// Schedule a flush by counting pending mutations. The timer drives the actual write
		// so we don't block recording with I/O. If we accumulate enough pending events,
		// flush immediately to bound buffer growth.
		var pending = Interlocked.Increment(ref this._pendingMutations);
		if (pending >= this._options.FlushOnPendingCount)
		{
			_ = Task.Run(this.FlushAsync, this._disposalCts.Token);
		}
	}

	/// <summary>
	/// Loads existing state from disk if present. Idempotent — only the first call does work.
	/// Failure is logged but not propagated; a missing/corrupt file degrades to "start fresh."
	/// </summary>
	private void EnsureLoaded()
	{
		if (this._loaded)
		{
			return;
		}

		lock (this._flushLock)
		{
			if (this._loaded)
			{
				return;
			}
			this._loaded = true;

			if (!File.Exists(this._options.Path))
			{
				return;
			}

			try
			{
				var bytes = File.ReadAllBytes(this._options.Path);
				if (bytes.Length == 0)
				{
					return;
				}

				var root = JsonNode.Parse(bytes) as JsonObject;
				if (root?["tools"] is not JsonObject tools)
				{
					return;
				}

				foreach (var (toolName, valueNode) in tools)
				{
					if (valueNode is not JsonObject toolObj)
					{
						continue;
					}

					this.SeedStats(
						toolName,
						totalCompressions: toolObj["total_compressions"]?.GetValue<long>() ?? 0,
						totalRetrievals: toolObj["total_retrievals"]?.GetValue<long>() ?? 0,
						fullRetrievals: toolObj["full_retrievals"]?.GetValue<long>() ?? 0,
						searchRetrievals: toolObj["search_retrievals"]?.GetValue<long>() ?? 0,
						budgetExhausted: toolObj["budget_exhausted"]?.GetValue<long>() ?? 0);

					var manualOverrideStr = toolObj["manual_override"]?.GetValue<string>();
					if (manualOverrideStr is not null
						&& Enum.TryParse<FeedbackOverride>(manualOverrideStr, ignoreCase: true, out var manualOverride)
						&& manualOverride != FeedbackOverride.None)
					{
						this.SetManualOverride(toolName, manualOverride);
					}
				}

				// Reset the dirty counter since we just loaded from disk.
				Interlocked.Exchange(ref this._pendingMutations, 0);
			}
			catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
			{
				LogLoadFailed(this._logger, ex, this._options.Path);
			}
		}
	}

	/// <summary>
	/// Writes the current in-memory state to disk atomically. Safe to call concurrently —
	/// internal lock serializes writes.
	/// </summary>
	public async Task FlushAsync()
	{
		this.EnsureLoaded();

		// Snapshot pending count BEFORE serializing so writes happening during flush still
		// schedule a follow-up flush. If pending was already 0, nothing changed since last
		// flush — skip the I/O entirely.
		var pendingAtStart = Interlocked.Exchange(ref this._pendingMutations, 0);
		if (pendingAtStart == 0)
		{
			return;
		}

		string serialized;
		lock (this._flushLock)
		{
			var root = new JsonObject
			{
				["version"] = 1,
				["updated_at"] = DateTimeOffset.UtcNow.ToString("O"),
				["tools"] = SerializeEntries(this.Entries),
			};
			serialized = root.ToJsonString(s_jsonOptions);
		}

		try
		{
			var directory = Path.GetDirectoryName(this._options.Path);
			if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
			{
				Directory.CreateDirectory(directory);
			}

			var tempPath = this._options.Path + ".tmp";
			await File.WriteAllTextAsync(tempPath, serialized).ConfigureAwait(false);
			File.Move(tempPath, this._options.Path, overwrite: true);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			LogFlushFailed(this._logger, ex, this._options.Path);
			// Restore the pending counter so the next tick retries.
			Interlocked.Add(ref this._pendingMutations, pendingAtStart);
		}
	}

	private static JsonObject SerializeEntries(IReadOnlyDictionary<string, ToolEntry> entries)
	{
		var tools = new JsonObject();
		foreach (var (name, entry) in entries)
		{
			var obj = new JsonObject
			{
				["total_compressions"] = entry.TotalCompressions,
				["total_retrievals"] = entry.TotalRetrievals,
				["full_retrievals"] = entry.FullRetrievals,
				["search_retrievals"] = entry.SearchRetrievals,
				["budget_exhausted"] = entry.BudgetExhausted,
			};

			if (entry.ManualOverride != FeedbackOverride.None)
			{
				obj["manual_override"] = entry.ManualOverride.ToString();
			}

			if (entry.LastEventUtc.HasValue)
			{
				obj["last_event"] = entry.LastEventUtc.Value.ToString("O");
			}

			tools[name] = obj;
		}
		return tools;
	}

	private void OnFlushTimerTick(object? state)
	{
		if (this._disposed)
		{
			return;
		}

		_ = this.FlushAsync();
	}

	/// <inheritdoc/>
	public void Dispose()
	{
		this.DisposeAsync().AsTask().GetAwaiter().GetResult();
	}

	/// <inheritdoc/>
	public async ValueTask DisposeAsync()
	{
		if (this._disposed)
		{
			return;
		}
		this._disposed = true;

		await this._flushTimer.DisposeAsync().ConfigureAwait(false);

		try
		{
			// Final flush so anything pending lands before we go away.
			await this.FlushAsync().ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			LogFlushFailed(this._logger, ex, this._options.Path);
		}

		this._disposalCts.Cancel();
		this._disposalCts.Dispose();
	}

	[LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to load feedback state from {Path}; starting fresh.")]
	private static partial void LogLoadFailed(ILogger logger, Exception exception, string path);

	[LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Failed to flush feedback state to {Path}; pending events retained for next flush.")]
	private static partial void LogFlushFailed(ILogger logger, Exception exception, string path);
}
