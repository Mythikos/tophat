using TopHat.Feedback;
using Xunit;

namespace TopHat.Tests.Feedback;

public sealed class InMemoryCompressionFeedbackStoreTests
{
	[Fact]
	public void RecordCompression_IncrementsTotalCompressions()
	{
		var store = new InMemoryCompressionFeedbackStore();

		store.RecordCompression("get_logs");
		store.RecordCompression("get_logs");
		store.RecordCompression("get_logs");

		var stats = store.GetStats("get_logs");
		Assert.NotNull(stats);
		Assert.Equal(3, stats.TotalCompressions);
		Assert.Equal(0, stats.TotalRetrievals);
	}

	[Fact]
	public void RecordRetrieval_IncrementsKindAndTotal()
	{
		var store = new InMemoryCompressionFeedbackStore();

		store.RecordRetrieval("get_logs", RetrievalKind.Search);
		store.RecordRetrieval("get_logs", RetrievalKind.Full);
		store.RecordRetrieval("get_logs", RetrievalKind.Full);
		store.RecordRetrieval("get_logs", RetrievalKind.BudgetExhausted);

		var stats = store.GetStats("get_logs")!;
		Assert.Equal(4, stats.TotalRetrievals);
		Assert.Equal(1, stats.SearchRetrievals);
		Assert.Equal(2, stats.FullRetrievals);
		Assert.Equal(1, stats.BudgetExhausted);
	}

	[Fact]
	public void NullOrEmptyToolName_IsSilentlyDropped()
	{
		// Best-effort recording: missing tool name shouldn't poison stats with a sentinel.
		var store = new InMemoryCompressionFeedbackStore();

		store.RecordCompression(null);
		store.RecordCompression("");
		store.RecordRetrieval(null, RetrievalKind.Full);
		store.RecordRetrieval("", RetrievalKind.Search);

		Assert.Null(store.GetStats("anything"));
	}

	[Fact]
	public void GetStats_ReturnsNullForUnseenTool()
	{
		var store = new InMemoryCompressionFeedbackStore();
		Assert.Null(store.GetStats("never_recorded"));
	}

	[Fact]
	public void RetrievalRate_ComputesFromCounters()
	{
		var store = new InMemoryCompressionFeedbackStore();
		for (var i = 0; i < 10; i++) store.RecordCompression("t");
		for (var i = 0; i < 4; i++) store.RecordRetrieval("t", RetrievalKind.Full);

		var stats = store.GetStats("t")!;
		Assert.Equal(0.4, stats.RetrievalRate, precision: 5);
	}

	[Fact]
	public void FullRetrievalRate_FoldsBudgetExhaustedIntoNumerator()
	{
		var store = new InMemoryCompressionFeedbackStore();
		// 2 Full + 1 BudgetExhausted + 1 Search = 4 total. Full+Budget = 3 → rate 0.75.
		store.RecordRetrieval("t", RetrievalKind.Full);
		store.RecordRetrieval("t", RetrievalKind.Full);
		store.RecordRetrieval("t", RetrievalKind.BudgetExhausted);
		store.RecordRetrieval("t", RetrievalKind.Search);

		var stats = store.GetStats("t")!;
		Assert.Equal(0.75, stats.FullRetrievalRate, precision: 5);
	}

	[Fact]
	public void SetManualOverride_ReadsBackOnStats()
	{
		var store = new InMemoryCompressionFeedbackStore();
		store.SetManualOverride("get_orders", FeedbackOverride.SkipCompression);

		var stats = store.GetStats("get_orders")!;
		Assert.Equal(FeedbackOverride.SkipCompression, stats.ManualOverride);
	}

	[Fact]
	public void SeedStats_PrepopulatesCountersWithoutSamples()
	{
		var store = new InMemoryCompressionFeedbackStore();
		store.SeedStats("seeded_tool", totalCompressions: 100, totalRetrievals: 80, fullRetrievals: 70, searchRetrievals: 8, budgetExhausted: 2);

		var stats = store.GetStats("seeded_tool")!;
		Assert.Equal(100, stats.TotalCompressions);
		Assert.Equal(80, stats.TotalRetrievals);
		Assert.Equal(70, stats.FullRetrievals);
	}

	[Fact]
	public async Task Concurrent_RecordsAreThreadSafe()
	{
		var store = new InMemoryCompressionFeedbackStore();
		var tasks = new Task[20];
		for (var i = 0; i < tasks.Length; i++)
		{
			tasks[i] = Task.Run(() =>
			{
				for (var j = 0; j < 100; j++) store.RecordCompression("hot_tool");
			});
		}
		await Task.WhenAll(tasks);

		Assert.Equal(2000, store.GetStats("hot_tool")!.TotalCompressions);
	}
}
