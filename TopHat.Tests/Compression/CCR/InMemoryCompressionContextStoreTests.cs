using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using TopHat.Compression.CCR;
using Xunit;

namespace TopHat.Tests.Compression.CCR;

public sealed class InMemoryCompressionContextStoreTests
{
	private static InMemoryCompressionContextStore Build(TimeSpan? retention = null, FakeTimeProvider? time = null)
	{
		var opts = Options.Create(new CCROptions { RetentionDuration = retention ?? TimeSpan.FromHours(1) });
		return new InMemoryCompressionContextStore(opts, time);
	}

	private static JsonObject Item(int id, string msg) =>
		new () { ["id"] = id, ["message"] = msg };

	[Fact]
	public void Retrieve_UnknownKey_ReturnsEmpty()
	{
		var store = Build();
		var result = store.Retrieve("missing", ids: null, limit: 10);
		Assert.Empty(result);
	}

	[Fact]
	public void Retrieve_WithoutIdFilter_ReturnsUpToLimit()
	{
		var store = Build();
		var items = new JsonNode[] { Item(1, "a"), Item(2, "b"), Item(3, "c") };
		store.Store("k", items);

		var result = store.Retrieve("k", ids: null, limit: 2);

		Assert.Equal(2, result.Count);
		Assert.Equal(1, result[0]!["id"]!.GetValue<int>());
		Assert.Equal(2, result[1]!["id"]!.GetValue<int>());
	}

	[Fact]
	public void Retrieve_WithIdFilter_OnlyReturnsMatches()
	{
		var store = Build();
		var items = new JsonNode[] { Item(10, "a"), Item(42, "b"), Item(99, "c") };
		store.Store("k", items);

		var result = store.Retrieve("k", ids: new HashSet<int> { 42, 99 }, limit: 100);

		Assert.Equal(2, result.Count);
		Assert.Equal(42, result[0]!["id"]!.GetValue<int>());
		Assert.Equal(99, result[1]!["id"]!.GetValue<int>());
	}

	[Fact]
	public void Retrieve_AfterTtlExpired_ReturnsEmpty()
	{
		var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
		var store = Build(retention: TimeSpan.FromMinutes(10), time: time);
		store.Store("k", new JsonNode[] { Item(1, "a") });

		time.Advance(TimeSpan.FromMinutes(11));

		var result = store.Retrieve("k", ids: null, limit: 10);
		Assert.Empty(result);
	}

	[Fact]
	public void Retrieve_ReturnsIndependentClones()
	{
		// Retrieved nodes must have no parent so consumers can splice them into their own docs.
		var store = Build();
		store.Store("k", new JsonNode[] { Item(1, "orig") });

		var first = store.Retrieve("k", ids: null, limit: 10);
		var second = store.Retrieve("k", ids: null, limit: 10);

		// Two separate retrievals return distinct clones (each JsonNode can have only one parent,
		// so a shared reference would throw when the consumer wraps it in its own parent).
		Assert.NotSame(first[0], second[0]);
		Assert.Null(first[0].Parent);
		Assert.Null(second[0].Parent);
	}

	[Fact]
	public void Retrieve_NonZeroLimitOnUnknownKey_DoesNotAllocate()
	{
		var store = Build();
		var result = store.Retrieve("missing", ids: null, limit: 100);
		Assert.Same(Array.Empty<JsonNode>(), result);
	}

	[Fact]
	public void Retrieve_ZeroLimit_ReturnsEmpty()
	{
		var store = Build();
		store.Store("k", new JsonNode[] { Item(1, "a") });

		var result = store.Retrieve("k", ids: null, limit: 0);
		Assert.Empty(result);
	}

	[Fact]
	public void Store_OverwritesExistingKey()
	{
		var store = Build();
		store.Store("k", new JsonNode[] { Item(1, "first") });
		store.Store("k", new JsonNode[] { Item(2, "second") });

		var result = store.Retrieve("k", ids: null, limit: 10);

		Assert.Single(result);
		Assert.Equal(2, result[0]!["id"]!.GetValue<int>());
	}

	private sealed class FakeTimeProvider : TimeProvider
	{
		private DateTimeOffset _now;

		public FakeTimeProvider(DateTimeOffset now)
		{
			_now = now;
		}

		public void Advance(TimeSpan delta) => _now += delta;

		public override DateTimeOffset GetUtcNow() => _now;
	}
}
