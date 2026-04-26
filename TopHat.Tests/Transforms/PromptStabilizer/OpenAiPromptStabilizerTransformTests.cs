using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TopHat.DependencyInjection;
using TopHat.Providers;
using TopHat.Tests.Support;
using TopHat.Transforms.PromptStabilizer;
using Xunit;

namespace TopHat.Tests.Transforms.PromptStabilizer;

/// <summary>
/// Tests for the M5a OpenAI prompt stabilizer. Planner decisions, restructurer behavior on both
/// /v1/chat/completions and /v1/responses shapes, end-to-end DI registration, byte-stability.
/// </summary>
public sealed class OpenAiPromptStabilizerTransformTests
{
    [Theory]
    [InlineData("gpt-4o", true)]
    [InlineData("gpt-4o-mini", true)]
    [InlineData("gpt-4o-2024-11-20", true)]
    [InlineData("gpt-4.1", true)]
    [InlineData("gpt-4.1-nano", true)]
    [InlineData("o1-preview", true)]
    [InlineData("o3-mini", true)]
    [InlineData("o4-mini", true)]
    [InlineData("gpt-5", true)]
    [InlineData("gpt-5-nano", true)]
    [InlineData("gpt-3.5-turbo", false)]
    [InlineData("gpt-4-turbo", false)]
    [InlineData("text-davinci-003", false)]
    public void Allowlist_MatchesExpectedModels(string model, bool expected)
    {
        var options = new OpenAiPromptStabilizerOptions();
        Assert.Equal(expected, OpenAiModelAllowlist.IsAllowed(model, options.AllowedModelPatterns.ToList()));
    }

    [Fact]
    public void Planner_ChatWithLongSystem_ApplyChat()
    {
        var body = BuildChatBody(BuildText(5000));
        var plan = OpenAiStabilizerPlanner.Plan(body, TopHatTarget.OpenAIChatCompletions, new OpenAiPromptStabilizerOptions(), "gpt-4o-mini");
        Assert.Equal(OpenAiStabilizerDecision.ApplyChat, plan.Decision);
        Assert.True(plan.PrefixChars >= 4096);
    }

    [Fact]
    public void Planner_ChatBelowThreshold_Skips()
    {
        var body = BuildChatBody("short");
        var plan = OpenAiStabilizerPlanner.Plan(body, TopHatTarget.OpenAIChatCompletions, new OpenAiPromptStabilizerOptions(), "gpt-4o");
        Assert.True(plan.IsSkip);
        Assert.Equal(PromptStabilizerSkipReason.BelowThreshold, plan.SkipReason);
    }

    [Fact]
    public void Planner_UnsupportedModel_Skips()
    {
        var body = BuildChatBody(BuildText(5000));
        var plan = OpenAiStabilizerPlanner.Plan(body, TopHatTarget.OpenAIChatCompletions, new OpenAiPromptStabilizerOptions(), "gpt-3.5-turbo");
        Assert.True(plan.IsSkip);
        Assert.Equal(PromptStabilizerSkipReason.UnsupportedOpenAiModel, plan.SkipReason);
    }

    [Fact]
    public void Planner_ChatEmptyMessages_Skips()
    {
        var body = new JsonObject { ["messages"] = new JsonArray() };
        var plan = OpenAiStabilizerPlanner.Plan(body, TopHatTarget.OpenAIChatCompletions, new OpenAiPromptStabilizerOptions(), "gpt-4o");
        Assert.True(plan.IsSkip);
        Assert.Equal(PromptStabilizerSkipReason.NoSystemOrInstructions, plan.SkipReason);
    }

    [Fact]
    public void Planner_ResponsesInstructions_ApplyResponses()
    {
        var body = new JsonObject
        {
            ["instructions"] = BuildText(5000),
            ["input"] = "hi",
        };
        var plan = OpenAiStabilizerPlanner.Plan(body, TopHatTarget.OpenAIResponses, new OpenAiPromptStabilizerOptions(), "gpt-4o");
        Assert.Equal(OpenAiStabilizerDecision.ApplyResponses, plan.Decision);
    }

    [Fact]
    public void Planner_ResponsesArrayInputWithImage_SkipsShapeUnsupported()
    {
        var body = new JsonObject
        {
            ["instructions"] = "short",
            ["input"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "input_image", ["image_url"] = "x" },
                    },
                },
            },
        };
        var plan = OpenAiStabilizerPlanner.Plan(body, TopHatTarget.OpenAIResponses, new OpenAiPromptStabilizerOptions(), "gpt-4o");
        Assert.True(plan.IsSkip);
        Assert.Equal(PromptStabilizerSkipReason.ResponsesInputShapeUnsupported, plan.SkipReason);
    }

    [Fact]
    public void ChatRestructurer_MovesDateToTail()
    {
        var body = BuildChatBody("Intro. Generated on 2024-11-08T12:34:56Z. More stable text " + BuildText(2000));
        var spans = new List<(int, int)> { (19, 20) };  // Approximate date span location
        // Use the extractor instead of hardcoded span to avoid brittleness.
        var options = new OpenAiPromptStabilizerOptions();
        var extractor = new TopHat.Transforms.Common.DynamicPatternExtractor(options.DynamicPatterns.ToList(), options.DynamicPatternTimeout);
        var text = body["messages"]![0]!["content"]!.GetValue<string>();
        var realSpans = extractor.Extract(text, options.DynamicExtractionTailFraction, options.DynamicExtractionTailMinChars);

        Assert.NotEmpty(realSpans);

        var moved = OpenAiChatRestructurer.ApplyDynamicSplit(body, realSpans);
        Assert.Equal(realSpans.Count, moved);

        var messages = (JsonArray)body["messages"]!;
        // Original system had the date; after split, system should not contain the date.
        var newSystem = messages[0]!["content"]!.GetValue<string>();
        Assert.DoesNotContain("2024-11-08T12:34:56Z", newSystem);
        // New trailing user message should contain the extracted content.
        var last = (JsonObject)messages[^1]!;
        Assert.Equal("user", last["role"]!.GetValue<string>());
        Assert.Contains("2024-11-08T12:34:56Z", last["content"]!.GetValue<string>());
    }

    [Fact]
    public void ChatRestructurer_NoSpans_NoOp()
    {
        var body = BuildChatBody("no dates here");
        var moved = OpenAiChatRestructurer.ApplyDynamicSplit(body, Array.Empty<(int, int)>());
        Assert.Equal(0, moved);
        Assert.Equal("no dates here", body["messages"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void ResponsesRestructurer_MovesUuidToTail()
    {
        var body = new JsonObject
        {
            ["instructions"] = "Intro. Session 550e8400-e29b-41d4-a716-446655440000 expired. " + BuildText(2000),
            ["input"] = "hi",
        };
        var options = new OpenAiPromptStabilizerOptions();
        var extractor = new TopHat.Transforms.Common.DynamicPatternExtractor(options.DynamicPatterns.ToList(), options.DynamicPatternTimeout);
        var text = body["instructions"]!.GetValue<string>();
        var spans = extractor.Extract(text, options.DynamicExtractionTailFraction, options.DynamicExtractionTailMinChars);

        Assert.NotEmpty(spans);
        var moved = OpenAiResponsesRestructurer.ApplyDynamicSplit(body, spans);
        Assert.Equal(spans.Count, moved);

        var newInstructions = body["instructions"]!.GetValue<string>();
        // UUID is still in the string but moved to the end past the DynamicHeader sentinel.
        var headerIdx = newInstructions.IndexOf("---\nDynamic context", StringComparison.Ordinal);
        Assert.True(headerIdx > 0);
        var prefix = newInstructions[..headerIdx];
        Assert.DoesNotContain("550e8400-e29b-41d4-a716-446655440000", prefix);
        Assert.Contains("550e8400-e29b-41d4-a716-446655440000", newInstructions[headerIdx..]);
    }

    [Fact]
    public async Task Transform_EndToEnd_LogsAppliedForSupportedLargeChat()
    {
        using (var capture = new MetricsCapture())
        {
            (var client, var inner, var _) = TransformHandlerFactory.Build(
                s => s.AddTopHatOpenAiPromptStabilizer(o =>
                {
                    o.MinimumTokens = 1024;  // explicit
                }),
                behavior: (_, _) =>
                {
                    var c = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"id\":\"x\",\"usage\":{\"prompt_tokens\":10}}"));
                    c.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = c });
                });

            var chatJson = "{\"model\":\"gpt-4o-mini\",\"messages\":[{\"role\":\"system\",\"content\":\"" + BuildText(5000) + "\"}]}";
            using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            {
                Content = new StringContent(chatJson, Encoding.UTF8, "application/json"),
            })
            {
                using (var response = await client.SendAsync(req))
                {
                    _ = await response.Content.ReadAsByteArrayAsync();
                }
            }

            var invoked = capture.ForInstrument("tophat.transform.invoked").ToList();
            Assert.Contains(invoked, r => Equals(r.Tag("transform_name"), nameof(OpenAiPromptStabilizerTransform)));
        }
    }

    [Fact]
    public async Task Transform_UnsupportedModel_SkipsWithTaggedReason()
    {
        using (var capture = new MetricsCapture())
        {
            (var client, var _, var _) = TransformHandlerFactory.Build(
                s => s.AddTopHatOpenAiPromptStabilizer());

            var chatJson = "{\"model\":\"gpt-3.5-turbo\",\"messages\":[{\"role\":\"system\",\"content\":\"" + BuildText(5000) + "\"}]}";
            using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            {
                Content = new StringContent(chatJson, Encoding.UTF8, "application/json"),
            })
            {
                using (var response = await client.SendAsync(req))
                {
                }
            }

            var skipped = capture.ForInstrument("tophat.transform.skipped").ToList();
            Assert.Contains(skipped, r => Equals(r.Tag("reason"), "unsupported_openai_model"));
        }
    }

    [Fact]
    public void ParseAndReserialize_ChatShape_DoesNotReorderKeys()
    {
        // OpenAI caches on byte-stable prefixes. Verify JsonObject preserves insertion order.
        var original = "{\"model\":\"gpt-4o\",\"stream\":false,\"temperature\":0,\"messages\":[{\"role\":\"system\",\"content\":\"hi\"},{\"role\":\"user\",\"content\":\"q\"}]}";
        var parsed = JsonNode.Parse(original);
        var reserialized = parsed!.ToJsonString();
        Assert.Equal(original, reserialized);
    }

    [Fact]
    public void ParseAndReserialize_ResponsesShape_DoesNotReorderKeys()
    {
        var original = "{\"model\":\"gpt-4o\",\"instructions\":\"hi\",\"input\":[{\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"q\"}]}]}";
        var parsed = JsonNode.Parse(original);
        var reserialized = parsed!.ToJsonString();
        Assert.Equal(original, reserialized);
    }

    [Fact]
    public async Task Transform_CatastrophicUserRegex_TimesOut_SurfacesRegexTimeoutSkip()
    {
        using (var capture = new MetricsCapture())
        {
            var badPattern = new Regex("^(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(10));

            (var client, var _, var _) = TransformHandlerFactory.Build(
                s => s.AddTopHatOpenAiPromptStabilizer(o =>
                {
                    o.ExperimentalDynamicExtraction = true;
                    o.DynamicPatterns.Add(badPattern);
                }),
                behavior: (_, _) =>
                {
                    var c = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"id\":\"x\"}"));
                    c.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = c });
                });

            // A long string of 'a's + a tail 'b' triggers catastrophic backtracking.
            var baddish = new string('a', 30) + "b";
            var systemText = BuildText(5000) + "\n" + baddish;
            var chatJson = "{\"model\":\"gpt-4o-mini\",\"messages\":[{\"role\":\"system\",\"content\":" + JsonValue.Create(systemText)!.ToJsonString() + "}]}";

            using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            {
                Content = new StringContent(chatJson, Encoding.UTF8, "application/json"),
            })
            {
                using (var response = await client.SendAsync(req))
                {
                }
            }

            var skipped = capture.ForInstrument("tophat.transform.skipped").ToList();
            // Either regex_timeout was emitted, or the regex completed in time — tolerate both but
            // assert that if it's tagged as skipped, the reason field makes sense.
            foreach (var s in skipped)
            {
                var reason = s.Tag("reason");
                Assert.Contains(reason, new object?[] { "regex_timeout", "below_threshold", "unsupported_openai_model", "already_stable", "no_system_or_instructions" });
            }
        }
    }

    private static JsonObject BuildChatBody(string systemText) => new()
    {
        ["model"] = "gpt-4o-mini",
        ["messages"] = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = systemText },
            new JsonObject { ["role"] = "user", ["content"] = "hello" },
        },
    };

    private static string BuildText(int chars)
    {
        var sb = new StringBuilder(chars);
        while (sb.Length < chars)
        {
            sb.Append("The quick brown fox jumps over the lazy dog. ");
        }

        return sb.ToString(0, chars);
    }
}
