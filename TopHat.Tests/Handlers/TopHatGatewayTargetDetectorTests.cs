using Microsoft.Extensions.Options;
using TopHat.Configuration;
using TopHat.Providers;
using Xunit;

namespace TopHat.Tests.Handlers;

/// <summary>Protects provider identity when gateways share hosts or mount provider APIs below a path prefix.</summary>
public sealed class TopHatGatewayTargetDetectorTests
{
	/// <summary>Shared-host routes need an unambiguous provider endpoint; unrelated path prefixes do not establish ownership.</summary>
	[Theory]
	[InlineData("/v1/messages", TopHatProviderKind.Anthropic, TopHatTarget.AnthropicMessages)]
	[InlineData("/anthropic/v1/messages", TopHatProviderKind.Anthropic, TopHatTarget.AnthropicMessages)]
	[InlineData("/anthropic/v1/messages/count_tokens", TopHatProviderKind.Anthropic, TopHatTarget.AnthropicCountTokens)]
	[InlineData("/anthropic/v1/messages/batches/batch_1/results", TopHatProviderKind.Anthropic, TopHatTarget.AnthropicBatches)]
	[InlineData("/v1/chat/completions", TopHatProviderKind.OpenAI, TopHatTarget.OpenAIChatCompletions)]
	[InlineData("/openai/v1/chat/completions", TopHatProviderKind.OpenAI, TopHatTarget.OpenAIChatCompletions)]
	[InlineData("/v1/responses", TopHatProviderKind.OpenAI, TopHatTarget.OpenAIResponses)]
	[InlineData("/openai/v1/responses/resp_1/input_items", TopHatProviderKind.OpenAI, TopHatTarget.OpenAIResponses)]
	[InlineData("/openai/v1/batches/batch_1", TopHatProviderKind.OpenAI, TopHatTarget.OpenAIBatches)]
	[InlineData("/v1/future-endpoint", TopHatProviderKind.Other, TopHatTarget.Unknown)]
	[InlineData("/openai-extra/v1/responses", TopHatProviderKind.Other, TopHatTarget.Unknown)]
	[InlineData("/openai/v1/responses-extra", TopHatProviderKind.Other, TopHatTarget.Unknown)]
	[InlineData("/anthropic/v1/messages/batches-extra", TopHatProviderKind.Other, TopHatTarget.Unknown)]
	[InlineData("/v1/messages/batches-extra", TopHatProviderKind.Other, TopHatTarget.Unknown)]
	[InlineData("/v1/responses-extra", TopHatProviderKind.Other, TopHatTarget.Unknown)]
	public void SharedGateway_UsesRecognizedProviderEndpoint(string path, TopHatProviderKind expectedProvider, TopHatTarget expectedTarget)
	{
		var detector = new TopHatTargetDetector(Options.Create(new TopHatOptions
		{
			AnthropicBaseUrl = new Uri("https://gateway.test/anthropic"),
			OpenAiBaseUrl = new Uri("https://gateway.test/openai/"),
		}));
		using (var request = new HttpRequestMessage(HttpMethod.Post, "https://gateway.test" + path))
		{
			Assert.Equal(expectedProvider, detector.DetectProvider(request));
			Assert.Equal(expectedTarget, detector.DetectTarget(request));
		}
	}

	/// <summary>Canonical hosts establish identity even if a provider's configured gateway happens to use the other canonical host.</summary>
	[Theory]
	[InlineData("https://api.openai.com/v1/chat/completions", TopHatProviderKind.OpenAI, TopHatTarget.OpenAIChatCompletions)]
	[InlineData("https://api.openai.com/v1/future-endpoint", TopHatProviderKind.OpenAI, TopHatTarget.Unknown)]
	[InlineData("https://api.anthropic.com/v1/messages", TopHatProviderKind.Anthropic, TopHatTarget.AnthropicMessages)]
	[InlineData("https://api.anthropic.com/v1/future-endpoint", TopHatProviderKind.Anthropic, TopHatTarget.Unknown)]
	public void CanonicalHost_TakesPrecedenceOverConfiguredGatewayAlias(string url, TopHatProviderKind expectedProvider, TopHatTarget expectedTarget)
	{
		var detector = new TopHatTargetDetector(Options.Create(new TopHatOptions
		{
			AnthropicBaseUrl = new Uri("https://api.openai.com"),
			OpenAiBaseUrl = new Uri("https://api.anthropic.com"),
		}));
		using (var request = new HttpRequestMessage(HttpMethod.Post, url))
		{
			Assert.Equal(expectedProvider, detector.DetectProvider(request));
			Assert.Equal(expectedTarget, detector.DetectTarget(request));
		}
	}

	/// <summary>Unknown paths keep existing single-provider host behavior, and configured prefixes never apply to unrelated hosts or sibling segments.</summary>
	[Theory]
	[InlineData("https://anthropic.gateway.test/new-api", TopHatProviderKind.Anthropic, TopHatTarget.Unknown)]
	[InlineData("https://openai.gateway.test/new-api", TopHatProviderKind.OpenAI, TopHatTarget.Unknown)]
	[InlineData("https://openai.gateway.test/openai-extra/v1/responses", TopHatProviderKind.OpenAI, TopHatTarget.Unknown)]
	[InlineData("https://openai.gateway.test/openai/v1/responses", TopHatProviderKind.OpenAI, TopHatTarget.OpenAIResponses)]
	[InlineData("https://anthropic.gateway.test/anthropic/v1/messages", TopHatProviderKind.Anthropic, TopHatTarget.AnthropicMessages)]
	[InlineData("https://api.openai.com/openai/v1/responses", TopHatProviderKind.OpenAI, TopHatTarget.Unknown)]
	[InlineData("https://unrelated.test/openai/v1/responses", TopHatProviderKind.Other, TopHatTarget.Unknown)]
	public void UniqueProviderGateway_PreservesHostAndPrefixBoundaries(string url, TopHatProviderKind expectedProvider, TopHatTarget expectedTarget)
	{
		var detector = new TopHatTargetDetector(Options.Create(new TopHatOptions
		{
			AnthropicBaseUrl = new Uri("https://anthropic.gateway.test/anthropic/"),
			OpenAiBaseUrl = new Uri("https://openai.gateway.test/openai"),
		}));
		using (var request = new HttpRequestMessage(HttpMethod.Post, url))
		{
			Assert.Equal(expectedProvider, detector.DetectProvider(request));
			Assert.Equal(expectedTarget, detector.DetectTarget(request));
		}
	}

	/// <summary>A common /v1 base path must not hide an already recognized provider endpoint.</summary>
	[Theory]
	[InlineData("https://openai.gateway.test/v1/chat/completions", TopHatTarget.OpenAIChatCompletions)]
	[InlineData("https://openai.gateway.test/v1/responses", TopHatTarget.OpenAIResponses)]
	[InlineData("https://anthropic.gateway.test/v1/messages", TopHatTarget.AnthropicMessages)]
	public void VersionBasePath_PreservesExistingEndpointShape(string url, TopHatTarget expectedTarget)
	{
		var detector = new TopHatTargetDetector(Options.Create(new TopHatOptions
		{
			AnthropicBaseUrl = new Uri("https://anthropic.gateway.test/v1"),
			OpenAiBaseUrl = new Uri("https://openai.gateway.test/v1/"),
		}));
		using (var request = new HttpRequestMessage(HttpMethod.Post, url))
		{
			Assert.Equal(expectedTarget, detector.DetectTarget(request));
		}
	}
}
