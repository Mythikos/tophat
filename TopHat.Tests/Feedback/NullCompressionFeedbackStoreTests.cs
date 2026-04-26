using TopHat.Feedback;
using Xunit;

namespace TopHat.Tests.Feedback;

public sealed class NullCompressionFeedbackStoreTests
{
	[Fact]
	public void AllOperations_AreNoOps()
	{
		var store = new NullCompressionFeedbackStore();

		// None of these should throw or accumulate state.
		store.RecordCompression("anything");
		store.RecordRetrieval("anything", RetrievalKind.Full);
		store.SetManualOverride("anything", FeedbackOverride.SkipCompression);
		store.SeedStats("anything", 100, 50, 40, 10, 0);

		Assert.Null(store.GetStats("anything"));
	}
}
