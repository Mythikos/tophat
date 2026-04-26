using TopHat.Transforms.JsonContext.Common;
using Xunit;

namespace TopHat.Tests.Transforms.JsonContext;

public sealed class ErrorKeywordDetectorTests
{
	[Theory]
	[InlineData("error")]
	[InlineData("exception")]
	[InlineData("failed")]
	[InlineData("failure")]
	[InlineData("critical")]
	[InlineData("fatal")]
	[InlineData("crash")]
	[InlineData("panic")]
	[InlineData("abort")]
	[InlineData("timeout")]
	[InlineData("denied")]
	[InlineData("rejected")]
	public void ContainsErrorKeyword_EachKeyword_Detected(string keyword)
	{
		Assert.True(ErrorKeywordDetector.ContainsErrorKeyword($"something {keyword} happened"));
	}

	[Fact]
	public void ContainsErrorKeyword_CaseInsensitive()
	{
		Assert.True(ErrorKeywordDetector.ContainsErrorKeyword("ERROR: disk full"));
		Assert.True(ErrorKeywordDetector.ContainsErrorKeyword("Fatal: out of memory"));
		Assert.True(ErrorKeywordDetector.ContainsErrorKeyword("TIMEOUT waiting for reply"));
	}

	[Fact]
	public void ContainsErrorKeyword_SubstringMatch()
	{
		// "errored" contains "error" as a substring.
		Assert.True(ErrorKeywordDetector.ContainsErrorKeyword("request errored out"));
		Assert.True(ErrorKeywordDetector.ContainsErrorKeyword("authentication failed with denied access"));
	}

	[Fact]
	public void ContainsErrorKeyword_NormalText_ReturnsFalse()
	{
		Assert.False(ErrorKeywordDetector.ContainsErrorKeyword("record inserted: 42 rows affected"));
		Assert.False(ErrorKeywordDetector.ContainsErrorKeyword("{\"status\":\"ok\",\"count\":100}"));
	}

	[Fact]
	public void ContainsErrorKeyword_EmptyString_ReturnsFalse()
	{
		Assert.False(ErrorKeywordDetector.ContainsErrorKeyword(string.Empty));
	}

	[Fact]
	public void FirstMatch_ReturnsFirstKeyword()
	{
		var result = ErrorKeywordDetector.FirstMatch("request timed out: timeout reached");
		Assert.Equal("timeout", result);
	}

	[Fact]
	public void FirstMatch_NoMatch_ReturnsNull()
	{
		Assert.Null(ErrorKeywordDetector.FirstMatch("all systems nominal"));
	}
}
