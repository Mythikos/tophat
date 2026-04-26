using TopHat.Transforms.JsonContext.Common;
using Xunit;

namespace TopHat.Tests.Transforms.JsonContext;

public sealed class QueryTermDetectorTests
{
	[Fact]
	public void ExtractTerms_EmptyOrWhitespace_ReturnsEmpty()
	{
		Assert.Empty(QueryTermDetector.ExtractTerms(string.Empty));
		Assert.Empty(QueryTermDetector.ExtractTerms("   "));
		Assert.Empty(QueryTermDetector.ExtractTerms(null!));
	}

	[Fact]
	public void ExtractTerms_QuotedString_IsExtracted()
	{
		var terms = QueryTermDetector.ExtractTerms("Which paths contain 'parseAuthToken'?");
		Assert.Contains("parseAuthToken", terms, StringComparer.Ordinal);
	}

	[Fact]
	public void ExtractTerms_DoubleQuotedString_IsExtracted()
	{
		var terms = QueryTermDetector.ExtractTerms("find records with name \"specific_alice\"");
		Assert.Contains("specific_alice", terms, StringComparer.Ordinal);
	}

	[Fact]
	public void ExtractTerms_BacktickQuotedString_IsExtracted()
	{
		var terms = QueryTermDetector.ExtractTerms("Check the `package.json` file");
		Assert.Contains("package.json", terms, StringComparer.Ordinal);
	}

	[Fact]
	public void ExtractTerms_CamelCaseIdentifier_IsExtracted()
	{
		var terms = QueryTermDetector.ExtractTerms("Find all usages of parseAuthToken in the codebase.");
		Assert.Contains("parseAuthToken", terms, StringComparer.Ordinal);
	}

	[Fact]
	public void ExtractTerms_SnakeCaseIdentifier_IsExtracted()
	{
		var terms = QueryTermDetector.ExtractTerms("How many requests had latency_ms above 500?");
		Assert.Contains("latency_ms", terms, StringComparer.Ordinal);
	}

	[Fact]
	public void ExtractTerms_PathLikeIdentifier_IsExtracted()
	{
		var terms = QueryTermDetector.ExtractTerms("Is src/auth/token.ts present?");
		Assert.Contains("src/auth/token.ts", terms, StringComparer.Ordinal);
	}

	[Fact]
	public void ExtractTerms_CommonEnglishWords_NotExtracted()
	{
		var terms = QueryTermDetector.ExtractTerms("Which file paths contain the string? List the paths.");
		// Plain lowercase words like "file", "paths", "contain" should not appear as terms.
		Assert.DoesNotContain("file", terms, StringComparer.OrdinalIgnoreCase);
		Assert.DoesNotContain("paths", terms, StringComparer.OrdinalIgnoreCase);
		Assert.DoesNotContain("contain", terms, StringComparer.OrdinalIgnoreCase);
	}

	[Fact]
	public void ExtractTerms_TooShortToken_NotExtracted()
	{
		var terms = QueryTermDetector.ExtractTerms("Get 'id' and 'ab'.");
		Assert.DoesNotContain("id", terms, StringComparer.Ordinal);
		Assert.DoesNotContain("ab", terms, StringComparer.Ordinal);
	}

	[Fact]
	public void ExtractTerms_DeduplicatesCaseInsensitive()
	{
		var terms = QueryTermDetector.ExtractTerms("Look for ParseAuthToken and 'parseAuthToken'.");
		// Two occurrences differ only in casing — should dedupe to a single term.
		Assert.Single(terms, t => string.Equals(t, "parseAuthToken", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void FindPreservedIndices_NoTerms_ReturnsEmpty()
	{
		var items = new[] { "alpha", "beta", "gamma" };
		var preserved = QueryTermDetector.FindPreservedIndices(items, Array.Empty<string>(), maxMatchesPerTerm: 10);
		Assert.Empty(preserved);
	}

	[Fact]
	public void FindPreservedIndices_MatchingTerm_PreservesMatches()
	{
		var items = new[]
		{
			"export function handle(req) { ... }",
			"export function parseAuthToken(raw) { ... }",
			"import { parseAuthToken } from './token';",
			"describe('parseAuthToken', () => ...);",
			"export function otherHandler() { ... }",
		};
		var preserved = QueryTermDetector.FindPreservedIndices(items, new[] { "parseAuthToken" }, maxMatchesPerTerm: 10);
		Assert.Contains(1, preserved);
		Assert.Contains(2, preserved);
		Assert.Contains(3, preserved);
		Assert.DoesNotContain(0, preserved);
		Assert.DoesNotContain(4, preserved);
	}

	[Fact]
	public void FindPreservedIndices_TermExceedsThreshold_IsSkipped()
	{
		// latency_ms appears in every item — threshold lower than match count → term dropped.
		var items = Enumerable.Range(0, 20)
			.Select(i => $"{{\"id\":{i},\"latency_ms\":{i * 10}}}")
			.ToArray();

		var preserved = QueryTermDetector.FindPreservedIndices(items, new[] { "latency_ms" }, maxMatchesPerTerm: 5);
		Assert.Empty(preserved);
	}

	[Fact]
	public void FindPreservedIndices_MixedTerms_OnlyInSignalOnesPreserved()
	{
		var items = Enumerable.Range(0, 20)
			.Select(i => i == 7 || i == 12
				? $"{{\"id\":{i},\"latency_ms\":{i * 10},\"tag\":\"parseAuthToken\"}}"
				: $"{{\"id\":{i},\"latency_ms\":{i * 10}}}")
			.ToArray();

		var preserved = QueryTermDetector.FindPreservedIndices(
			items,
			new[] { "latency_ms", "parseAuthToken" },
			maxMatchesPerTerm: 5);

		// latency_ms matches 20, exceeds threshold → dropped.
		// parseAuthToken matches 2 (ids 7 and 12), within threshold → preserved.
		Assert.Equal(2, preserved.Count);
		Assert.Contains(7, preserved);
		Assert.Contains(12, preserved);
	}

	[Fact]
	public void FindPreservedIndices_CaseInsensitiveMatch()
	{
		var items = new[]
		{
			"function PARSEAUTHTOKEN(x) { ... }",
			"nothing relevant",
		};

		var preserved = QueryTermDetector.FindPreservedIndices(items, new[] { "parseAuthToken" }, maxMatchesPerTerm: 10);
		Assert.Contains(0, preserved);
		Assert.DoesNotContain(1, preserved);
	}
}
