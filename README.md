# TopHat

**TopHat saves you money by optimizing the LLM context layer.**

It sits between your .NET application and Anthropic / OpenAI, intercepts outbound requests, and compresses tool-result payloads that would otherwise burn tokens unnecessarily. When the model needs items the compressor elided, TopHat fulfills the retrieval transparently — the caller only ever sees the final answer.

Across an 8-fixture eval against live providers (Anthropic Haiku 4.5 and OpenAI gpt-4o), with `aggregation-count-slow` opted out via the static override system:

| Provider | Baseline cost | With TopHat | Saved |
|---|---:|---:|---:|
| Anthropic | $0.0344 | $0.0157 | **54.3%** |
| OpenAI | $0.0669 | $0.0259 | **61.3%** |

Savings vary by workload. The eval covers needle-finding (find logs with errors), lookups (find user by id), grep-style searches, semantic queries, paraphrase recognition, and full-scan aggregations. TopHat handles the first five well; aggregations are explicitly opted out via the override system rather than fought.

---

## Table of contents

- [How it works](#how-it-works)
- [Quickstart](#quickstart)
- [Packages](#packages)
- [Use cases](#use-cases)
- [Compression & relevance](#compression--relevance)
- [CCR — Compression Context Retrieval](#ccr--compression-context-retrieval)
- [Feedback layer](#feedback-layer)
- [Tokenizers](#tokenizers)
- [Observability — OTel metrics](#observability--otel-metrics)
- [Configuration reference](#configuration-reference)
- [Running the eval](#running-the-eval)

---

## How it works

TopHat is a `DelegatingHandler` you wire into your `HttpClient` chain. Every request to a supported provider endpoint passes through it.

**Outbound (request side):**
1. Body is parsed into a JSON tree.
2. Tool-result content blocks are located.
3. Each block is scored against the user's query (BM25 / ONNX / fused).
4. Lower-relevance items are dropped, replaced with a structured summary; high-relevance items are kept.
5. A `_tophat_compression` metadata block is embedded in the compressed payload, including a retrieval key.
6. A `tophat_retrieve` tool is injected into the request's tool definitions so the model knows it can ask for elided items.

**Inbound (response side):**
1. Response is parsed; if it contains a `tophat_retrieve` tool call, CCR fulfills it from an in-memory store of dropped items and re-dispatches upstream.
2. The orchestrator loops up to `MaxIterations` (default 3) before giving up.
3. Cumulative token usage from all hops is summed into the final response's `usage` field so billing is accurate.
4. Caller sees only the final response; the retrieval round-trips are invisible.

Three targets are supported:

| Provider | Endpoint | Compression | CCR |
|---|---|:-:|:-:|
| Anthropic | `/v1/messages` | ✓ | ✓ |
| OpenAI | `/v1/chat/completions` | ✓ | ✓ |
| OpenAI | `/v1/responses` | ✓ | ✓ |

---

## Use cases

**TopHat helps when**:
- Tool calls return large, partially-relevant payloads (search results, log queries, API responses with many fields)
- The same payload structure is queried many ways (some queries need only a few records, some need broader context)
- You're paying real money on input tokens at scale
- You're running into context-window limits

**TopHat doesn't help (or hurts) when**:
- The model genuinely needs every item every time (full scans, aggregations, "count where X")
- Tool results are already small (under ~200 tokens — TopHat skips compression on these)
- The user's queries reliably touch the entire payload

For workloads that don't compress well, opt them out per-tool via the [feedback override system](#feedback-layer). TopHat's static skip-list and empirical learning both gracefully exclude these without needing a hard-coded bypass.

---

## Quickstart

Minimal setup — Anthropic Messages with BM25 keyword scoring:

```csharp
using Microsoft.Extensions.DependencyInjection;
using TopHat.DependencyInjection;
using TopHat.Handlers;
using TopHat.Relevance.BM25.DependencyInjection;
using TopHat.Transforms.JsonContext;

var services = new ServiceCollection();
services.AddTopHat();
services.AddTopHatBm25Relevance();
services.AddTopHatJsonContextCompressor();

services.AddHttpClient("anthropic", c =>
    {
        c.BaseAddress = new Uri("https://api.anthropic.com/");
        c.DefaultRequestHeaders.Add("x-api-key", apiKey);
        c.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    })
    .AddHttpMessageHandler<TopHatHandler>();

var client = services.BuildServiceProvider()
    .GetRequiredService<IHttpClientFactory>()
    .CreateClient("anthropic");

// Use `client` like any HttpClient — TopHat compresses tool_results in your requests.
```

Add CCR for retrieval-on-demand when the model needs items the compressor dropped:

```csharp
using TopHat.Compression.CCR.DependencyInjection;

services.AddTopHatCCR();
```

Add ONNX semantic scoring (better paraphrase / synonym handling). Any scorers registered alongside BM25 — ONNX, your own custom `IRelevanceScorer`, or both — are fused automatically:

```csharp
using TopHat.Relevance.Onnx.DependencyInjection;

services.AddTopHatOnnxRelevance(opt => opt.ModelDirectory = "/path/to/AllMiniLML6V2");
// Plug in a custom scorer the same way:
services.AddSingleton<IRelevanceScorer, MyDomainScorer>();
```

### Inspect feedback stats — observe what TopHat is learning

Recording is on by default. To inspect per-tool stats programmatically:

```csharp
using TopHat.Feedback;

var feedback = sp.GetRequiredService<ICompressionFeedbackStore>();
var stats = feedback.GetStats("get_logs");
if (stats is not null)
{
    Console.WriteLine($"Compressions: {stats.TotalCompressions}");
    Console.WriteLine($"Retrievals: {stats.TotalRetrievals}");
    Console.WriteLine($"  full: {stats.FullRetrievals}");
    Console.WriteLine($"  search: {stats.SearchRetrievals}");
    Console.WriteLine($"  budget exhausted: {stats.BudgetExhausted}");
    Console.WriteLine($"Retrieval rate: {stats.RetrievalRate:P0}");
    Console.WriteLine($"Full-retrieval rate: {stats.FullRetrievalRate:P0}");
}
```

A high retrieval rate (>50%) with a high full-retrieval rate (>80%) is the signal that compression is hurting more than helping for that tool — see [feedback behavior](#feedback-behavior--act-on-the-data) below.

To survive restarts (otherwise stats reset on every run):

```csharp
services.UseTopHatFileFeedbackStore();
// or with a custom path:
services.UseTopHatFileFeedbackStore(opt => opt.Path = "/var/lib/myapp/tophat-feedback.json");
```

### Feedback behavior — act on the data

Three independent ways to control compression per tool. They coexist; pick whichever fits your workflow.

**Static opt-out** — declare known cases at startup. No learning, no runtime code:

```csharp
services.UseTopHatFeedbackOverrides(opt =>
{
    opt.SkipCompressionFor("billing_query");
    opt.SkipCompressionFor("aggregation_report");
    opt.AlwaysCompressFor("search_results"); // force compression even if learning would skip
});
```

**Empirical learning** — let TopHat decide which tools to skip based on observed retrieval patterns:

```csharp
services.UseTopHatFeedbackDecisions();
// after ≥5 samples per tool, high-retrieval tools start skipping automatically
```

**Runtime override** — programmatic per-tool decisions (e.g., feature flag rollout):

```csharp
var feedback = sp.GetRequiredService<ICompressionFeedbackStore>();
feedback.SetManualOverride("get_orders", FeedbackOverride.SkipCompression);
```

Override precedence (highest wins): runtime store override → static config override → empirical thresholds → standard compression.

---

## Packages

| Package | Purpose | When to install |
|---|---|---|
| `TopHat` | Core: handler, transform pipeline, CCR, in-memory feedback store, file-backed feedback store, no-op feedback store, chars/4 tokenizer, OTel instruments | Always |
| `TopHat.Relevance.BM25` | Keyword-exact relevance scoring | When users do keyword/identifier searches |
| `TopHat.Relevance.Onnx` | Semantic scoring via MiniLM-L6-v2 ONNX model | When users do paraphrase / synonym queries; pairs with BM25 via fusion |
| `TopHat.Tokenizers.OpenAI` | Bit-exact tiktoken token counting (local, fast) | When OpenAI cost transparency matters |
| `TopHat.Tokenizers.Anthropic` | Bit-exact token counting via Anthropic's count_tokens API | When Anthropic cost transparency matters |

Storage variants for feedback (file-backed, in-memory, no-op) ship in core — no extra package needed since they have no external NuGet dependencies.

---

## Compression & relevance

The compressor (`JsonContextCompressorTransform`) operates on tool-result content. For each result above the size threshold, it:

1. **Parses** the content as JSON. If it isn't JSON, the result is left alone.
2. **Walks** the JSON for arrays of similar-shaped items (the case where compression has high value).
3. **Scores** each item against the user query via the registered `IRelevanceScorer`.
4. **Keeps** the top-N items by score (configurable via `MaxItemsAfterCrush`, default 15) plus boundary slots (first-K and last-K) for context preservation.
5. **Summarizes** the dropped items into a structured digest (counts by status, field statistics, etc.) and embeds it alongside the kept items.
6. **Embeds** a `_tophat_compression` metadata block with the retrieval key, summary, and counts so the model has visibility into what was elided.

### Relevance scorers

Built-in:

- **BM25** (`TopHat.Relevance.BM25`) — keyword-exact scoring. Fast, no model files, no external dependencies. Best for queries where the user's keywords match terms in the data verbatim.
- **ONNX MiniLM-L6-v2** (`TopHat.Relevance.Onnx`) — semantic scoring via a small embedding model. Captures paraphrase / synonym relationships BM25 misses. Requires the model files (`model.onnx` + `vocab.txt`) from sentence-transformers/all-MiniLM-L6-v2.

Custom scorers are first-class. The `IRelevanceScorer` interface is the only contract — implement it once, register the singleton, done:

```csharp
public sealed class MyDomainScorer : IRelevanceScorer
{
    public IReadOnlyList<double> Score(string queryContext, IReadOnlyList<string> items)
    {
        // Return per-item relevance scores in [0, 1]. Length must match items.Count.
    }
}

services.AddSingleton<IRelevanceScorer, MyDomainScorer>();
```

Use cases for custom scorers: domain-specific keyword indexes, larger embedding models you've already wired up elsewhere, learned rankers trained on your historical retrieval logs, lexical scorers for non-English content, or hybrid scorers using your own LLM.

**Automatic fusion**: when more than one `IRelevanceScorer` is registered, TopHat fuses them via `FusedRelevanceScorer` — each scorer's per-batch scores are min-max normalized to [0, 1] and summed per item. Higher-weighted opinions don't drown the others, and you don't have to choose one. This applies to ALL registered scorers (built-in or custom): ship BM25 + ONNX + your domain scorer together and they all contribute. A single registered scorer is used directly without fusion.

### Configuration

```csharp
services.AddTopHatJsonContextCompressor(opt =>
{
    opt.MaxItemsAfterCrush = 15; // top-K keep size
    opt.MinTokensToCrush = 200; // skip results below this size (chars/4 estimate)
    opt.UnfrozenMessageCount = 1; // last N messages are mutable; earlier ones are frozen for cache safety
    opt.BoundaryFirstK = 1; // always keep this many items from the start
    opt.BoundaryLastK = 1; // always keep this many items from the end
});
```

---

## CCR — Compression Context Retrieval

When the compressor drops items the model later realizes it needs, CCR fulfills the retrieval transparently. The model calls a synthetic `tophat_retrieve` tool (which TopHat injected into the request's tool list); CCR's orchestrator intercepts the call, looks up the dropped items by retrieval key, and continues the conversation with the items appended.

### Setup

```csharp
services.AddTopHatCCR();
```

Optional configuration:

```csharp
services.AddTopHatCCR(opt =>
{
    opt.MaxIterations = 3; // budget for retrieval rounds (initial + N follow-ups)
    opt.RetentionDuration = TimeSpan.FromHours(1); // how long to keep dropped items
    opt.RetrievalItemCeiling = 50; // max items returned per retrieval call
});
```

### When CCR fires

- Model emits a `tophat_retrieve` tool call → CCR fulfills + re-dispatches
- If model also emits a caller-defined tool call alongside `tophat_retrieve` → CCR passes through unchanged (caller's tool loop handles it)
- If iteration budget exhausted → CCR returns the last response as-is; caller may see an unfulfilled `tophat_retrieve` call (treat as a budget signal)

### Cost accounting

CCR multi-hop responses have their `usage.input_tokens` / `output_tokens` / `cache_*` fields rewritten to reflect cumulative cost across all hops, not just the last hop. An `X-TopHat-CCR-Hops` header surfaces the hop count. Caller code reading `response.usage` gets accurate billing without needing to know CCR happened.

---

## Feedback layer

The feedback layer lets TopHat learn (or be told) which tools shouldn't be compressed. Three independent mechanisms, all coexisting:

### 1. Static declarative overrides — recommended for known cases

```csharp
services.UseTopHatFeedbackOverrides(opt =>
{
    opt.SkipCompressionFor("billing_query");
    opt.SkipCompressionFor("aggregation_report");
    opt.AlwaysCompressFor("search_results");
});
```

Applies at every request — no learning needed, no decisions opt-in needed. Use when you already know certain tools' outputs need full inspection.

### 2. Empirical decisions — opt-in adaptive learning

```csharp
services.UseTopHatFeedbackDecisions();
```

Activates threshold-based decisions. The compressor consults accumulated stats per tool and skips compression when:
- ≥5 samples accumulated for the tool, AND
- Retrieval rate >50% (compressor's drops triggered retrievals more than half the time), AND
- Full-retrieval rate >80% (most retrievals asked for everything)

Tunable thresholds:

```csharp
services.UseTopHatFeedbackDecisions(opt =>
{
    opt.MinSamplesForHints = 10; // bump cold-start gate
    opt.HighRetrievalThreshold = 0.4; // tighten skip threshold
    opt.FullRetrievalThreshold = 0.7;
});
```

### 3. Runtime overrides via the store API

```csharp
var store = sp.GetRequiredService<ICompressionFeedbackStore>();
store.SetManualOverride("get_orders", FeedbackOverride.SkipCompression);
```

Useful for dynamic per-request decisions or programmatic tuning.

### Storage choices

```csharp
// Default — InMemory, lost on restart, auto-registered by AddTopHat()
services.AddTopHat();

// File-backed — JSON file with batched async flush + atomic writes
services.UseTopHatFileFeedbackStore(opt =>
{
    opt.Path = "/var/lib/myapp/tophat-feedback.json";
    opt.FlushInterval = TimeSpan.FromSeconds(10);
});

// No-op — recording disabled (privacy / perf paranoia)
services.UseTopHatNoopFeedbackStore();
```

### Override precedence

Highest wins:
1. Runtime store override (most recent intent)
2. Static config override (deployed declaration)
3. Empirical thresholds (only if `UseTopHatFeedbackDecisions()` registered)
4. Standard compression

---

## Tokenizers

For pre/post token-count metrics to be accurate, register a provider-specific tokenizer. Without one, TopHat falls back to a chars/4 approximation that overshoots actual JSON token counts by ~20-30%.

```csharp
// OpenAI: bit-exact tiktoken, local, sub-millisecond per call
services.AddTopHatOpenAITokenizer();

// Anthropic: bit-exact via /v1/messages/count_tokens API
services.AddTopHatAnthropicTokenizer(opt =>
{
    opt.ApiKey = anthropicApiKey;
    opt.Mode = AnthropicTokenizerMode.Deferred; // default — fire-and-forget
});
```

The Anthropic tokenizer makes a network round-trip per token-count operation. **Deferred mode** (default) returns 0 synchronously and emits the metric out-of-band when the API call completes — zero added latency on the request path, metrics arrive slightly after. **Blocking mode** awaits inline (~50-100ms per request) for accurate per-request reduction-ratio histograms; use when correlation matters more than throughput.

When neither tokenizer is registered, TopHat uses the in-core `CharsPerTokenTokenizer` and tags emitted metrics with `tokenizer_kind=chars_per_token` so dashboards can distinguish approximations from authoritative numbers.

---

## Observability — OTel metrics

TopHat emits on the `TopHat` Meter (`TopHatMetrics.MeterName`). To consume:

```csharp
using OpenTelemetry.Metrics;
using TopHat.Diagnostics;

services.AddOpenTelemetry()
    .WithMetrics(b => b
        .AddMeter(TopHatMetrics.MeterName)
        .AddOtlpExporter());
```

For non-host console apps, build the provider directly via `Sdk.CreateMeterProviderBuilder()` since hosted services don't activate the periodic exporter.

### Metric catalog

#### Request lifecycle

| Metric | Type | Tags | Description |
|---|---|---|---|
| `tophat.requests` | Counter | target, method, status_code, streaming, bypass, model | Every request TopHat intercepts |
| `tophat.request.ttfb` | Histogram | target, status_code | Time to first byte (ms) |
| `tophat.request.duration` | Histogram | target | Total request duration including stream close (ms) |
| `tophat.request.bytes` | Histogram | target | Request Content-Length post-transform |
| `tophat.response.bytes` | Histogram | target | Response Content-Length when known |
| `tophat.upstream.errors` | Counter | target, kind | Upstream errors classified by kind |

#### Cost transparency (the headline numbers)

| Metric | Type | Tags | Description |
|---|---|---|---|
| `tophat.request.tokens.pre_transform` | Counter | target, model, tokenizer_kind | Tokens in request body BEFORE TopHat ran — what a no-TopHat baseline would have sent |
| `tophat.request.tokens.post_transform` | Counter | target, model, tokenizer_kind | Tokens in request body AFTER TopHat ran — what TopHat dispatched on the first hop |
| `tophat.compression.payload.reduction.ratio` | Histogram | target, tokenizer_kind | Per-request fraction reduced: (pre − post) / pre |
| `tophat.tokens.input` | Counter | target, model | What upstream actually billed for input (sum across CCR hops) |
| `tophat.tokens.output` | Counter | target, model | What upstream actually billed for output |
| `tophat.tokens.cache_read` | Counter | target, model | Cache-read tokens (Anthropic prompt caching, OpenAI prefix caching) |
| `tophat.tokens.cache_creation` | Counter | target, model | Cache-creation tokens |

**Three layers of truth**:
- `pre_transform` = "what would I have sent without TopHat?"
- `post_transform` = "what did TopHat compress it to?"
- `tokens.input` = "what did I actually get billed?"

Compression-only savings: `pre − post`. Net savings vs no-TopHat: `pre − tokens.input` (accounts for CCR overhead).

#### CCR

| Metric | Type | Tags | Description |
|---|---|---|---|
| `tophat.ccr.orchestrations` | Counter | target, outcome | CCR runs by outcome: single_hop, multi_hop, foreign_tool_use, budget_exhausted, not_orchestratable, parse_failure |
| `tophat.ccr.hops` | Histogram | target | Upstream hop count per CCR-orchestrated request (initial dispatch counts as hop 1) |

#### Cache integrity

| Metric | Type | Tags | Description |
|---|---|---|---|
| `tophat.cache.busts_detected` | Counter | target, transform_name, model | A transform mutated cache-relevant content. Anthropic uses `cache_control` markers; OpenAI uses "everything except the last conversation entry." Should stay zero in steady state. |

#### Transforms

| Metric | Type | Tags | Description |
|---|---|---|---|
| `tophat.transform.invoked` | Counter | target, transform_name, phase | Per-transform invocation count |
| `tophat.transform.mutated` | Counter | target, transform_name, phase | Transforms that actually modified the request |
| `tophat.transform.skipped` | Counter | target, transform_name, reason | Transforms that decided not to run, classified by reason |
| `tophat.transform.errors` | Counter | target, transform_name, kind, failure_mode, phase | Transform exceptions |

#### Streaming internals

| Metric | Type | Tags | Description |
|---|---|---|---|
| `tophat.stream.outcome` | Counter | outcome | Response stream close outcomes: eof, disposed, error |
| `tophat.parser.errors` | Counter | kind | Response-observer parser errors. Never propagated to the caller. |

### Building cost dashboards

Common queries:

```promql
# Estimated baseline cost (what you would have spent without TopHat)
sum by (model) (rate(tophat_request_tokens_pre_transform_total[5m])) * <input_rate_per_token>

# Actual billed cost
sum by (model) (rate(tophat_tokens_input_total[5m])) * <input_rate_per_token>

# Net savings rate
(pre_transform_total − tokens_input_total) × $rate

# Cache-bust rate (should be 0)
sum by (target, transform_name) (rate(tophat_cache_busts_detected_total[5m]))

# CCR firing rate by outcome
sum by (target, outcome) (rate(tophat_ccr_orchestrations_total[5m]))
```

---

## Configuration reference

```csharp
services.AddTopHat(); // core: handler + transform registry

// Relevance scoring (need at least one for compression)
services.AddTopHatBm25Relevance();
services.AddTopHatOnnxRelevance(opt => opt.ModelDirectory = "/path/to/model");

// Compression
services.AddTopHatJsonContextCompressor(opt =>
{
    opt.MaxItemsAfterCrush = 15;
    opt.MinTokensToCrush = 200;
    opt.UnfrozenMessageCount = 1;
});

// CCR — opt-in retrieval-on-demand
services.AddTopHatCCR(opt =>
{
    opt.MaxIterations = 3;
    opt.RetentionDuration = TimeSpan.FromHours(1);
    opt.RetrievalItemCeiling = 50;
});

// Tokenizers — opt-in for accurate token counts
services.AddTopHatOpenAITokenizer();
services.AddTopHatAnthropicTokenizer(opt =>
{
    opt.ApiKey = anthropicApiKey;
    opt.Mode = AnthropicTokenizerMode.Deferred;
});

// Feedback storage — InMemory is the default, swap if needed
services.UseTopHatFileFeedbackStore(opt => opt.Path = "...");
services.UseTopHatNoopFeedbackStore();

// Feedback overrides — declarative skip / always-compress per tool
services.UseTopHatFeedbackOverrides(opt =>
{
    opt.SkipCompressionFor("aggregation_report");
    opt.AlwaysCompressFor("search_results");
});

// Feedback decisions — empirical adaptive learning
services.UseTopHatFeedbackDecisions(opt =>
{
    opt.MinSamplesForHints = 5;
    opt.HighRetrievalThreshold = 0.5;
    opt.FullRetrievalThreshold = 0.8;
});
```

---

## Running the eval

The `TopHat.Samples.CompressionEval` project sends 8 fixtures to live providers under both baseline and compressed conditions, then writes a transcript for qualitative review and prints feedback stats:

```bash
dotnet run --project Resources/Samples/TopHat.Samples.CompressionEval -- \
    --provider anthropic \
    --scorer hybrid \
    --ccr \
    --enable-telemetry \
    --samples 6 \
    --model-dir /path/to/AllMiniLML6V2
```

Flags:
- `--provider anthropic | openai`
- `--scorer bm25 | onnx | hybrid`
- `--ccr` — enable CCR
- `--enable-telemetry` — export OTel metrics to OTLP at `localhost:18889` (Aspire Dashboard)
- `--feedback-decisions` — enable empirical learning layer
- `--samples N` — samples per condition (default 3; needs ≥6 to exercise empirical decisions with default thresholds)

Set `ANTHROPIC_API_KEY` or `OPENAI_API_KEY` accordingly. Eval costs ~$0.05 per Anthropic run and ~$0.10 per OpenAI run.

For the Aspire Dashboard:

```bash
cd Resources/Samples/TopHat.Samples.OtelMonitoringDemo
docker compose up -d
# Dashboard at http://localhost:18888
```

The `TopHat.Samples.OtelMonitoringDemo` project itself runs synthetic CCR scenarios against an in-process fake LLM if you want to see metrics flow without spending real tokens.