using System.Text.Json.Nodes;
using TopHat.Compression.CCR;
using Xunit;

namespace TopHat.Tests.Compression.CCR;

public sealed class CCRUsageMergerTests
{
	[Fact]
	public void Accumulate_SumsTopLevelNumerics()
	{
		var accumulator = new JsonObject();
		var first = new JsonObject { ["input_tokens"] = 100, ["output_tokens"] = 50 };
		var second = new JsonObject { ["input_tokens"] = 200, ["output_tokens"] = 25 };

		CCRUsageMerger.Accumulate(accumulator, first);
		CCRUsageMerger.Accumulate(accumulator, second);

		Assert.Equal(300, accumulator["input_tokens"]!.GetValue<long>());
		Assert.Equal(75, accumulator["output_tokens"]!.GetValue<long>());
	}

	[Fact]
	public void Accumulate_RecursesIntoNestedObjects()
	{
		// OpenAI Chat shape: prompt_tokens + nested prompt_tokens_details with cached_tokens.
		var accumulator = new JsonObject();
		var first = new JsonObject
		{
			["prompt_tokens"] = 100,
			["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = 40 },
		};
		var second = new JsonObject
		{
			["prompt_tokens"] = 250,
			["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = 60 },
		};

		CCRUsageMerger.Accumulate(accumulator, first);
		CCRUsageMerger.Accumulate(accumulator, second);

		Assert.Equal(350, accumulator["prompt_tokens"]!.GetValue<long>());
		Assert.Equal(100, accumulator["prompt_tokens_details"]!["cached_tokens"]!.GetValue<long>());
	}

	[Fact]
	public void Accumulate_IgnoresNonNumericFields()
	{
		// service_tier is a string in OpenAI responses — must be silently dropped, not crash.
		var accumulator = new JsonObject();
		var incoming = new JsonObject
		{
			["prompt_tokens"] = 100,
			["service_tier"] = "default",
			["model_label"] = "gpt-4o",
		};

		CCRUsageMerger.Accumulate(accumulator, incoming);

		Assert.Equal(100, accumulator["prompt_tokens"]!.GetValue<long>());
		Assert.Null(accumulator["service_tier"]);
		Assert.Null(accumulator["model_label"]);
	}

	[Fact]
	public void Accumulate_NullIncoming_NoOp()
	{
		var accumulator = new JsonObject { ["x"] = 5L };

		CCRUsageMerger.Accumulate(accumulator, null);

		Assert.Equal(5, accumulator["x"]!.GetValue<long>());
	}

	[Fact]
	public async Task ApplyAsync_RewritesUsageAndStampsHopHeader()
	{
		var accumulator = new JsonObject { ["input_tokens"] = 500, ["output_tokens"] = 100 };
		var response = new HttpResponseMessage
		{
			Content = new StringContent(
				"""{"id":"msg_01","usage":{"input_tokens":200,"output_tokens":40}}""",
				System.Text.Encoding.UTF8, "application/json"),
		};

		await CCRUsageMerger.ApplyAsync(response, accumulator, hopCount: 3, CancellationToken.None);

		Assert.Equal("3", response.Headers.GetValues(CCRUsageMerger.HopCountHeader).Single());
		var rewritten = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
		Assert.Equal(500, rewritten["usage"]!["input_tokens"]!.GetValue<long>());
		Assert.Equal(100, rewritten["usage"]!["output_tokens"]!.GetValue<long>());
		Assert.Equal("msg_01", rewritten["id"]!.GetValue<string>());
	}

	[Fact]
	public async Task ApplyAsync_NonJsonBody_StampsHeaderOnly()
	{
		var accumulator = new JsonObject { ["x"] = 1 };
		var response = new HttpResponseMessage
		{
			Content = new StringContent("not json at all", System.Text.Encoding.UTF8, "text/plain"),
		};

		await CCRUsageMerger.ApplyAsync(response, accumulator, hopCount: 2, CancellationToken.None);

		Assert.Equal("2", response.Headers.GetValues(CCRUsageMerger.HopCountHeader).Single());
		// Body untouched on parse failure.
		Assert.Equal("not json at all", await response.Content.ReadAsStringAsync());
	}
}
