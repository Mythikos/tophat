using Microsoft.Extensions.Options;
using System.Text.Json.Nodes;
using TopHat.Relevance;
using TopHat.Relevance.BM25;
using TopHat.Transforms.JsonContext;
using TopHat.Transforms.JsonContext.Strategies;
using TopHat.Transforms.JsonContext.Summarization;
using Xunit;

namespace TopHat.Tests.Transforms.JsonContext;

public sealed class DictArrayCompressionStrategyTests
{
	private static JsonCompressionContext MakeCtx(string queryContext = "", IReadOnlyList<IDroppedItemsSummarizer>? summarizers = null) =>
		new ()
		{
			QueryContext = queryContext,
			Scorer = new BM25Scorer(),
			Options = new JsonContextCompressorOptions { MaxItemsAfterCrush = 15 },
			Summarizers = summarizers ?? Array.Empty<IDroppedItemsSummarizer>(),
		};

	private static JsonObject MakeEntry(int id, string status = "ok", string? note = null)
	{
		var obj = new JsonObject { ["id"] = id, ["status"] = status };

		if (note is not null)
		{
			obj["note"] = note;
		}

		return obj;
	}

	private static JsonNode?[] MakeItems(int count, string status = "ok") =>
		Enumerable.Range(1, count).Select(i => (JsonNode?)MakeEntry(i, status)).ToArray();

	[Fact]
	public void Compress_SmallArray_NoModification()
	{
		var items = MakeItems(4);
		var (_, modified) = DictArrayCompressionStrategy.Compress(items, MakeCtx());
		Assert.False(modified);
	}

	[Fact]
	public void Compress_LargeArray_ReducesCount()
	{
		var items = MakeItems(100);
		var (kept, modified) = DictArrayCompressionStrategy.Compress(items, MakeCtx());
		Assert.True(modified);
		Assert.True(kept.Count < 100);
		Assert.True(kept.Count <= 15);
	}

	[Fact]
	public void Compress_AlwaysPreservesErrorItems()
	{
		var values = MakeItems(60).ToList();
		values.Add(MakeEntry(999, "failed", "error: disk full"));
		var (kept, _) = DictArrayCompressionStrategy.Compress(values, MakeCtx());

		var keptObjs = kept.Cast<JsonObject>().ToArray();
		Assert.Contains(keptObjs, obj => obj["id"]!.GetValue<int>() == 999);
	}

	[Fact]
	public void Compress_PreservesFirstAndLastItems()
	{
		var items = MakeItems(80);
		var (kept, _) = DictArrayCompressionStrategy.Compress(items, MakeCtx());

		var keptIds = kept.Cast<JsonObject>().Select(obj => obj["id"]!.GetValue<int>()).ToArray();
		Assert.Contains(1, keptIds);   // First item.
		Assert.Contains(80, keptIds);  // Last item.
	}

	[Fact]
	public void Compress_WithQueryContext_PreservesRelevantItems()
	{
		// Items have UUIDs; one UUID appears in the query — that item should be preserved.
		const string targetId = "550e8400-e29b-41d4-a716-446655440000";
		var values = Enumerable.Range(1, 50)
			.Select(i =>
			{
				var obj = new JsonObject { ["index"] = i, ["guid"] = Guid.NewGuid().ToString() };
				return (JsonNode?)obj;
			})
			.ToList();

		// Insert the target UUID in a middle item (index 25).
		((JsonObject)values[24]!)["guid"] = targetId;

		var ctx = MakeCtx($"find record {targetId}");
		var (kept, _) = DictArrayCompressionStrategy.Compress(values, ctx);

		var keptItems = kept.Cast<JsonObject>().ToArray();
		Assert.Contains(keptItems, obj => obj["guid"]?.GetValue<string>() == targetId);
	}

	[Fact]
	public void Compress_ResultItemsAreInOriginalOrder()
	{
		var items = MakeItems(60);
		var (kept, _) = DictArrayCompressionStrategy.Compress(items, MakeCtx());

		var keptIds = kept.Cast<JsonObject>().Select(obj => obj["id"]!.GetValue<int>()).ToArray();
		Assert.Equal(keptIds, keptIds.OrderBy(i => i).ToArray());
	}

	[Fact]
	public void Compress_RespectsMaxItemsAfterCrush()
	{
		var items = MakeItems(200);
		var (kept, _) = DictArrayCompressionStrategy.Compress(items, MakeCtx());
		Assert.True(kept.Count <= 15, $"Expected ≤15 items, got {kept.Count}");
	}

	[Fact]
	public void Compress_WithSummarizers_AppendsMetadataObject()
	{
		var items = new List<JsonNode?>();

		for (var i = 1; i <= 100; i++)
		{
			items.Add(MakeEntry(i, "INFO"));
		}

		var summarizers = new IDroppedItemsSummarizer[]
		{
			new CategoricalSummarizer(Options.Create(new CategoricalSummarizerOptions())),
		};
		var ctx = MakeCtx(summarizers: summarizers);

		var (kept, modified) = DictArrayCompressionStrategy.Compress(items, ctx);

		Assert.True(modified);
		var last = kept[^1] as JsonObject;
		Assert.NotNull(last);
		Assert.True(last!.ContainsKey(DictArrayCompressionStrategy.CompressionMetadataKey));

		var metadata = last[DictArrayCompressionStrategy.CompressionMetadataKey] as JsonObject;
		Assert.NotNull(metadata);
		Assert.True(metadata!["omitted"]!.GetValue<int>() > 0);
		Assert.True(metadata["kept"]!.GetValue<int>() > 0);
		Assert.NotNull(metadata["summary"]);
	}

	[Fact]
	public void Compress_WithoutSummarizers_NoMetadataAppended()
	{
		var items = MakeItems(100);
		var (kept, _) = DictArrayCompressionStrategy.Compress(items, MakeCtx());

		foreach (var node in kept)
		{
			if (node is JsonObject obj)
			{
				Assert.False(obj.ContainsKey(DictArrayCompressionStrategy.CompressionMetadataKey));
			}
		}
	}

	[Fact]
	public void Compress_PreservesItemsMatchingLiteralQueryTerm()
	{
		// Reproduces the grep-fixture bug: BM25 length-normalization was dropping id=88 (longer
		// 'describe' snippet) even though it contains the exact query identifier. The
		// QueryTermDetector path must preserve all three literal matches.
		var items = new List<JsonNode?>();

		for (var i = 0; i < 100; i++)
		{
			if (i == 17)
			{
				items.Add(new JsonObject
				{
					["id"] = i,
					["path"] = "src/auth/token.ts",
					["snippet"] = "export function parseAuthToken(raw: string): Token { ... }",
				});
			}
			else if (i == 54)
			{
				items.Add(new JsonObject
				{
					["id"] = i,
					["path"] = "src/middleware/auth.ts",
					["snippet"] = "import { parseAuthToken } from '../auth/token';",
				});
			}
			else if (i == 88)
			{
				items.Add(new JsonObject
				{
					["id"] = i,
					["path"] = "test/auth/token.test.ts",
					["snippet"] = "describe('parseAuthToken', () => { it('rejects empty', () => { ... }); });",
				});
			}
			else
			{
				items.Add(new JsonObject
				{
					["id"] = i,
					["path"] = $"src/feature_{i}/module.ts",
					["snippet"] = "export function handle(req: Request) { return ok(); }",
				});
			}
		}

		var ctx = MakeCtx("Which file paths contain the string 'parseAuthToken'?");
		var (kept, _) = DictArrayCompressionStrategy.Compress(items, ctx);

		var keptIds = kept.OfType<JsonObject>()
			.Where(o => o["id"] is not null)
			.Select(o => o["id"]!.GetValue<int>())
			.ToHashSet();

		Assert.Contains(17, keptIds);
		Assert.Contains(54, keptIds);
		Assert.Contains(88, keptIds);
	}

	[Fact]
	public void Compress_WithSummarizers_NoCompression_NoMetadataAppended()
	{
		var items = MakeItems(4);  // Below MinItemsToAnalyze.
		var summarizers = new IDroppedItemsSummarizer[]
		{
			new CategoricalSummarizer(Options.Create(new CategoricalSummarizerOptions())),
		};

		var (kept, modified) = DictArrayCompressionStrategy.Compress(items, MakeCtx(summarizers: summarizers));

		Assert.False(modified);
		Assert.Equal(4, kept.Count);
	}
}
