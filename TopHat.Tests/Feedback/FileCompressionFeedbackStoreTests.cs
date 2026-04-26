using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TopHat.Feedback;
using Xunit;

namespace TopHat.Tests.Feedback;

/// <summary>
/// Tests the file-backed feedback store's persistence shape: write to disk, read back into a
/// fresh instance, atomic replacement, manual override roundtrip. Uses a per-test temp path
/// so tests don't interfere.
/// </summary>
public sealed class FileCompressionFeedbackStoreTests : IDisposable
{
	private readonly string _tempPath;

	public FileCompressionFeedbackStoreTests()
	{
		this._tempPath = Path.Combine(Path.GetTempPath(), $"tophat-feedback-test-{Guid.NewGuid():N}.json");
	}

	public void Dispose()
	{
		if (File.Exists(this._tempPath))
		{
			File.Delete(this._tempPath);
		}
	}

	private FileCompressionFeedbackStore BuildStore()
	{
		var options = Options.Create(new FileFeedbackOptions { Path = this._tempPath });
		return new FileCompressionFeedbackStore(options, NullLogger<FileCompressionFeedbackStore>.Instance);
	}

	[Fact]
	public async Task RecordsRoundTripThroughDisk()
	{
		// Write events through one instance, dispose to flush, read through a fresh instance.
		await using (var store = this.BuildStore())
		{
			store.RecordCompression("get_logs");
			store.RecordCompression("get_logs");
			store.RecordRetrieval("get_logs", RetrievalKind.Full);
			store.RecordRetrieval("get_logs", RetrievalKind.Search);
			await store.FlushAsync();
		}

		// File should exist with our data.
		Assert.True(File.Exists(this._tempPath));

		await using var fresh = this.BuildStore();
		var stats = fresh.GetStats("get_logs");
		Assert.NotNull(stats);
		Assert.Equal(2, stats.TotalCompressions);
		Assert.Equal(2, stats.TotalRetrievals);
		Assert.Equal(1, stats.FullRetrievals);
		Assert.Equal(1, stats.SearchRetrievals);
	}

	[Fact]
	public async Task ManualOverride_PersistsAcrossInstances()
	{
		await using (var store = this.BuildStore())
		{
			store.SetManualOverride("get_orders", FeedbackOverride.SkipCompression);
			await store.FlushAsync();
		}

		await using var fresh = this.BuildStore();
		var stats = fresh.GetStats("get_orders")!;
		Assert.Equal(FeedbackOverride.SkipCompression, stats.ManualOverride);
	}

	[Fact]
	public async Task FlushAsync_NoOpsWhenNothingPending()
	{
		// Calling FlushAsync without any writes shouldn't create the file. Avoids polluting
		// disk on apps that register the store but never use it.
		await using var store = this.BuildStore();
		await store.FlushAsync();

		Assert.False(File.Exists(this._tempPath));
	}

	[Fact]
	public async Task LoadsExistingFileOnFirstRead()
	{
		// Pre-seed a file then build a fresh store — first GetStats should pick up the data.
		await File.WriteAllTextAsync(this._tempPath, """
			{
				"version": 1,
				"updated_at": "2026-04-23T00:00:00Z",
				"tools": {
					"preseeded_tool": {
						"total_compressions": 50,
						"total_retrievals": 5,
						"full_retrievals": 1,
						"search_retrievals": 4,
						"budget_exhausted": 0,
						"manual_override": "AlwaysCompress"
					}
				}
			}
			""");

		await using var store = this.BuildStore();
		var stats = store.GetStats("preseeded_tool");
		Assert.NotNull(stats);
		Assert.Equal(50, stats.TotalCompressions);
		Assert.Equal(FeedbackOverride.AlwaysCompress, stats.ManualOverride);
	}

	[Fact]
	public async Task CorruptFile_DegradesToFreshStart()
	{
		// Garbage on disk shouldn't crash the app — store should log and proceed empty.
		await File.WriteAllTextAsync(this._tempPath, "{ this is not valid json");

		await using var store = this.BuildStore();
		Assert.Null(store.GetStats("anything"));

		// Should still be able to record new events.
		store.RecordCompression("new_tool");
		Assert.Equal(1, store.GetStats("new_tool")!.TotalCompressions);
	}

	[Fact]
	public async Task DisposeFlushes()
	{
		// Ensures no event loss on graceful shutdown.
		await using (var store = this.BuildStore())
		{
			store.RecordCompression("on_dispose");
			// Don't call FlushAsync — Dispose should do it.
		}

		Assert.True(File.Exists(this._tempPath));
		await using var fresh = this.BuildStore();
		Assert.Equal(1, fresh.GetStats("on_dispose")!.TotalCompressions);
	}
}
