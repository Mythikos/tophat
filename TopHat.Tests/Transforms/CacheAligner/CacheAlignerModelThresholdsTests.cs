using TopHat.Transforms.CacheAligner;
using Xunit;

namespace TopHat.Tests.Transforms.CacheAligner;

public sealed class CacheAlignerModelThresholdsTests
{
    private static readonly string[] s_defaultAllowlist =
    [
        "claude-3-*",
        "claude-sonnet-4-*",
        "claude-opus-4-*",
        "claude-haiku-4-*",
        "claude-mythos-*",
    ];

    [Theory]
    [InlineData("claude-opus-4-5-20251001", 4096)]
    [InlineData("claude-opus-4-6-20251101", 4096)]
    [InlineData("claude-opus-4-7-20260101", 4096)]
    [InlineData("claude-haiku-4-5-20251001", 4096)]
    [InlineData("claude-mythos-preview-20260215", 4096)]
    [InlineData("claude-sonnet-4-6-20251101", 2048)]
    [InlineData("claude-sonnet-4-5-20250929", 1024)]
    [InlineData("claude-sonnet-4-20250514", 1024)]
    [InlineData("claude-sonnet-3-7-20250219", 1024)]
    [InlineData("claude-opus-4-1-20250805", 1024)]
    [InlineData("claude-opus-4-20250514", 1024)]
    [InlineData("claude-haiku-3-5-20241022", 2048)]
    [InlineData("claude-3-opus-20240229", 1024)]
    public void ExactPatternMatch_ReturnsRightMinimum(string model, int expected)
    {
        var ok = CacheAlignerModelThresholds.TryGetMinimumTokens(model, ExpandAllowlist(), out var min);
        Assert.True(ok);
        Assert.Equal(expected, min);
    }

    [Fact]
    public void ModelNotInAllowlist_ReturnsFalse()
    {
        var ok = CacheAlignerModelThresholds.TryGetMinimumTokens("gpt-4o", s_defaultAllowlist, out _);
        Assert.False(ok);
    }

    [Fact]
    public void EmptyModel_ReturnsFalse()
    {
        var ok = CacheAlignerModelThresholds.TryGetMinimumTokens(string.Empty, s_defaultAllowlist, out _);
        Assert.False(ok);
    }

    [Fact]
    public void AllowedButUnknownPattern_FallsBackToConservativeMinimum()
    {
        // Hypothetical future model not yet in the threshold table but matches the allowlist.
        var allowlist = new[] { "claude-hypothetical-*" };
        var ok = CacheAlignerModelThresholds.TryGetMinimumTokens("claude-hypothetical-5-0-20270101", allowlist, out var min);
        Assert.True(ok);
        Assert.Equal(4096, min);  // default unknown minimum
    }

    [Fact]
    public void ThresholdLookup_PrefixCollisions_ResolveToMostSpecific()
    {
        Assert.Equal(4096, MinFor("claude-opus-4-5-20251001"));   // specific 4-5
        Assert.Equal(1024, MinFor("claude-opus-4-20250514"));     // falls through to opus-4-*
        Assert.Equal(1024, MinFor("claude-opus-4-1-20250805"));   // specific 4-1
        Assert.Equal(1024, MinFor("claude-sonnet-4-5-20250929")); // specific 4-5
        Assert.Equal(2048, MinFor("claude-sonnet-4-6-20251101")); // specific 4-6
        Assert.Equal(4096, MinFor("claude-haiku-4-5-20251001"));  // specific 4-5, resolves ahead of haiku-4-*
    }

    [Fact]
    public void ModelWithoutTrailingHyphen_NotMatchedByDefaultPatterns()
    {
        // claude-opus-4 doesn't match claude-opus-4-* because there's no hyphen-tail.
        var ok = CacheAlignerModelThresholds.TryGetMinimumTokens("claude-opus-4", s_defaultAllowlist, out _);
        Assert.False(ok);
    }

    [Theory]
    [InlineData("foo", "foo", true)]
    [InlineData("foo", "*", true)]
    [InlineData("claude-sonnet-4-5-20250929", "claude-sonnet-4-5-*", true)]
    [InlineData("claude-sonnet-4-5", "claude-sonnet-4-5-*", false)]  // no trailing hyphen-star match
    [InlineData("claude-sonnet-4-5-x", "claude-sonnet-4-5-*", true)]
    [InlineData("foo-bar", "*", true)]
    [InlineData("foo bar", "foo*", false)]  // space is not in the allowed glob-char set
    public void GlobMatcher(string input, string pattern, bool expected)
    {
        Assert.Equal(expected, TopHat.Transforms.Common.ModelGlobMatcher.GlobMatch(input, pattern));
    }

    private static int MinFor(string model)
    {
        Assert.True(CacheAlignerModelThresholds.TryGetMinimumTokens(model, ExpandAllowlist(), out var min));
        return min;
    }

    // The threshold table has more patterns than the default allowlist; expand so tests can hit
    // exotic combinations without the allowlist itself becoming the limiting factor.
    private static string[] ExpandAllowlist() =>
    [
        "claude-3-*",
        "claude-sonnet-3-*",
        "claude-sonnet-4-*",
        "claude-opus-4-*",
        "claude-haiku-3-*",
        "claude-haiku-4-*",
        "claude-mythos-*",
    ];
}
