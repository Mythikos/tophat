using Microsoft.Extensions.Options;
using System.Text.Json.Nodes;
using TopHat.Transforms.JsonContext.Summarization;
using Xunit;

namespace TopHat.Tests.Transforms.JsonContext.Summarization;

public sealed class CategoricalSummarizerTests
{
	private static CategoricalSummarizer MakeSummarizer(CategoricalSummarizerOptions? opts = null) =>
		new (Options.Create(opts ?? new CategoricalSummarizerOptions()));

	private static SummarizationContext MakeContext(int dropped) =>
		new () { OriginalCount = dropped + 5, KeptCount = 5 };

	private static JsonObject Obj(params (string Key, object Value)[] fields)
	{
		var obj = new JsonObject();

		foreach (var (key, value) in fields)
		{
			obj[key] = JsonValue.Create(value);
		}

		return obj;
	}

	[Fact]
	public void Summarize_EmptyDropped_ReturnsNull()
	{
		var summarizer = MakeSummarizer();
		var result = summarizer.Summarize(Array.Empty<JsonObject>(), new SummarizationContext { OriginalCount = 5, KeptCount = 5 });
		Assert.Null(result);
	}

	[Fact]
	public void Summarize_CountsByLevelField()
	{
		var dropped = new List<JsonObject>();

		for (var i = 0; i < 50; i++)
		{
			dropped.Add(Obj(("id", i), ("level", "INFO")));
		}

		for (var i = 0; i < 5; i++)
		{
			dropped.Add(Obj(("id", 100 + i), ("level", "ERROR"), ("message", "boom")));
		}

		var summarizer = MakeSummarizer();
		var result = summarizer.Summarize(dropped, MakeContext(dropped.Count));

		Assert.NotNull(result);
		Assert.Contains("50 INFO", result);
		Assert.Contains("5 ERROR", result);
	}

	[Fact]
	public void Summarize_CountsByStatusWhenLevelMissing()
	{
		var dropped = new List<JsonObject>
		{
			Obj(("id", 1), ("status", "ok")),
			Obj(("id", 2), ("status", "ok")),
			Obj(("id", 3), ("status", "pending")),
		};

		var result = MakeSummarizer().Summarize(dropped, MakeContext(3));

		Assert.NotNull(result);
		Assert.Contains("2 ok", result);
		Assert.Contains("1 pending", result);
	}

	[Fact]
	public void Summarize_FirstMatchingCategoryFieldWins()
	{
		// 'type' comes before 'status' in the default CategoryFields order.
		var dropped = new List<JsonObject>
		{
			Obj(("id", 1), ("type", "log"), ("status", "ok")),
			Obj(("id", 2), ("type", "log"), ("status", "error")),
		};

		var result = MakeSummarizer().Summarize(dropped, MakeContext(2));

		Assert.NotNull(result);
		Assert.Contains("2 log", result);
		Assert.DoesNotContain("status", result, StringComparison.Ordinal);
	}

	[Fact]
	public void Summarize_TooLongCategoryValue_FallsBackToFallbackField()
	{
		var longString = new string('x', 100);
		var dropped = new List<JsonObject>
		{
			Obj(("id", 1), ("type", longString), ("tag", "alpha")),
			Obj(("id", 2), ("type", longString), ("tag", "alpha")),
		};

		var result = MakeSummarizer().Summarize(dropped, MakeContext(2));

		Assert.NotNull(result);
		Assert.Contains("tag=alpha", result);
	}

	[Fact]
	public void Summarize_NotableItemsMatchPatternAndLabelById()
	{
		var dropped = new List<JsonObject>
		{
			Obj(("id", 1), ("level", "INFO"), ("message", "hello")),
			Obj(("id", 2), ("level", "ERROR"), ("message", "disk failure")),
			Obj(("id", 3), ("level", "INFO"), ("message", "timeout reached")),
		};

		var result = MakeSummarizer().Summarize(dropped, MakeContext(3));

		Assert.NotNull(result);
		Assert.Contains("notable:", result);
		// At least one of the matching ids should appear labeled with a notable keyword.
		Assert.True(result.Contains("2 (error)", StringComparison.OrdinalIgnoreCase) ||
					result.Contains("3 (timeout)", StringComparison.OrdinalIgnoreCase),
			$"Expected notable to call out id 2 or 3 with keyword; got: {result}");
	}

	[Fact]
	public void Summarize_RespectsMaxCategories()
	{
		// Each category must have count >= 2 to clear the signal threshold.
		var dropped = new List<JsonObject>();

		foreach (var level in new[] { "A", "B", "C", "D", "E", "F", "G" })
		{
			dropped.Add(Obj(("id", 1), ("level", level)));
			dropped.Add(Obj(("id", 2), ("level", level)));
		}

		var opts = new CategoricalSummarizerOptions { MaxCategories = 3 };
		var result = MakeSummarizer(opts).Summarize(dropped, MakeContext(dropped.Count));

		Assert.NotNull(result);
		var countParts = result.Split(',').Length;
		Assert.True(countParts <= 3, $"Expected at most 3 categories; got '{result}'");
	}

	[Fact]
	public void Summarize_AllUniqueCategoryValues_SuppressesCategoricalNoise()
	{
		// Every username is unique — fallback path would emit "1 username=user_0, 1 username=user_1, ..."
		// which is pure noise. The summarizer should suppress that fragment.
		var dropped = new List<JsonObject>();

		for (var i = 0; i < 20; i++)
		{
			dropped.Add(new JsonObject { ["id"] = i, ["username"] = $"user_{i}" });
		}

		var result = MakeSummarizer().Summarize(dropped, MakeContext(dropped.Count));

		// May fall back to "fields: ..." but must NOT contain per-item "1 username=..." noise.
		Assert.False(result is not null && result.Contains("1 username=", StringComparison.Ordinal),
			$"Expected categorical noise to be suppressed; got: {result}");
	}

	[Fact]
	public void Summarize_RespectsMaxNotable()
	{
		var dropped = new List<JsonObject>();

		for (var i = 0; i < 10; i++)
		{
			dropped.Add(Obj(("id", i), ("message", "critical error")));
		}

		var opts = new CategoricalSummarizerOptions { MaxNotable = 2 };
		var result = MakeSummarizer(opts).Summarize(dropped, MakeContext(dropped.Count));

		Assert.NotNull(result);
		var notableIdx = result.IndexOf("notable:", StringComparison.Ordinal);
		Assert.True(notableIdx >= 0);

		var notablePart = result[notableIdx..];
		var semicolonCount = notablePart.Count(c => c == ';');
		// "notable: a (x); b (x)" has one ';' — bounded by MaxNotable - 1.
		Assert.True(semicolonCount <= 1, $"Expected at most 2 notable entries (1 separator); got '{notablePart}'");
	}

	[Fact]
	public void Summarize_NoCategoriesNoNotable_FallsBackToFieldList()
	{
		// All numeric fields — no string categories to group by, no notable keywords.
		// Fallback path inside _categorize_by_fields will try first non-id string fields;
		// if none match either, CommonKeys fallback kicks in.
		var dropped = new List<JsonObject>
		{
			Obj(("id", 1), ("value", 10.0), ("weight", 2.5)),
			Obj(("id", 2), ("value", 20.0), ("weight", 3.5)),
		};

		var result = MakeSummarizer().Summarize(dropped, MakeContext(2));

		Assert.NotNull(result);
		Assert.Contains("fields:", result);
		Assert.Contains("id", result);
	}
}
