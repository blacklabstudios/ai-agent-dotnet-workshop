# P5 Demo - Watch Every LLM Call With OpenTelemetry

> Pillar 5 demo. Facilitator drives, room watches, nobody types. Runs in the observability slot, right after P5.01.

## Mission

Make the agent report what it does. Every model call, every embedding, every tool call, and every token goes out over OpenTelemetry (OTel) to the Aspire Dashboard, a free OTLP viewer that runs in one container.

**Learning Objectives**:

- Turn on M.E.AI's built-in GenAI telemetry with `UseOpenTelemetry()`
- Export traces and metrics over OTLP to any backend
- Add the agent and tool spans a hand-written loop has to own itself
- Read token usage and latency per model call, per turn, and over time
- Add your own metric to turn tokens into an estimated cost, total and per model

---

## Prerequisites

- The repo is on the `p5-01-end` branch. Run `git status` and confirm the tree is clean.
- Run `dotnet build FinanceAssistant.sln`. It has to succeed with zero warnings.
- The `AzureOpenAI:*` user secrets for `finance-assistant-workshop` are set. Either path from P1.01 works.
- The pgvector Postgres container is running. Check it with `docker compose ps` and confirm the STATUS column reads `Up`.
- Port `4317` and port `18888` are free on your machine. The dashboard needs both.

> The finished state is on the `demo-p5-otel` branch. If the live demo breaks, `git checkout demo-p5-otel` and carry on from Step 6.

---

## What we're solving

Run the agent on `p5-01-end` and ask it one question. The console prints `[agent] iteration 1: calling SearchTransactions`, then a few more lines, then an answer. That's all you get.

You can't see how many model calls that took. You can't see how many tokens each call sent, which one was slow, or which tool returned the data the answer was built on. When the answer is wrong, you're guessing. When the bill arrives, you're guessing too.

The lecture named the tools: OTel and App Insights on the Microsoft stack, and Helicone, LangSmith, and Phoenix as third-party proxies. This demo takes the vendor-neutral one and makes it real, then uses it to answer the question everyone actually asks: what is this costing me?

OpenTelemetry is the standard here, and it has a vocabulary for this. The GenAI semantic conventions define the span names (`chat`, `embeddings`, `execute_tool`, `invoke_agent`), the attributes (`gen_ai.request.model`, `gen_ai.usage.input_tokens`), and the metrics (`gen_ai.client.token.usage`, `gen_ai.client.operation.duration`). Any backend that speaks OTLP can read them. That's the point. You instrument once and choose where to look later.

We'll do it in four pieces:

1. **Let M.E.AI emit.** `UseOpenTelemetry()` on the chat client and the embedding generator. That's every model call, with tokens and timings, for free.
2. **Ship it.** An OTel tracer provider and meter provider with an OTLP exporter. Without these, the spans are created and nobody listens.
3. **Own the loop's spans.** Our `ChatAgent` is hand-written, so nothing above the model call knows a "turn" happened. We add one `invoke_agent` span per turn and one `execute_tool` span per tool call.
4. **Put a price on it.** Tokens aren't money. A small middleware turns each call's usage into a cost, records it as a metric, and tags it on the spans.

---

## If you're comfortable, do this

Use this list if you want the route first. The full steps explain each choice and help you recover when one fails.

1. Start the dashboard: `docker run --rm -d --name aspire-dashboard -p 18888:18888 -p 4317:18889 -e ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true mcr.microsoft.com/dotnet/aspire-dashboard:13.6.1`. Open `http://localhost:18888`.
2. Add `OpenTelemetry.Exporter.OpenTelemetryProtocol` version `1.20.1` to `src/FinanceAssistant/FinanceAssistant.csproj`.
3. In `ServiceCollectionExtensions.cs`, add `.UseOpenTelemetry()` after `.ConfigureOptions(...)` on the chat client. Wrap the embedding generator in `.AsBuilder().UseOpenTelemetry().Build()`.
4. At the top of `Program.cs`, build a tracer provider and a meter provider. Both listen to `Experimental.Microsoft.Extensions.AI`, the tracer also listens to `FinanceAssistant`, and both export with `.AddOtlpExporter()`. Set the metric export interval to 5 seconds.
5. In `ChatAgent.cs`, add a static `ActivitySource` named `FinanceAssistant`. Start an `invoke_agent FinanceAssistant` activity at the top of `RunTurnAsync` and an `execute_tool {name}` activity around each `function.InvokeAsync`.
6. Run the agent, ask a question, and open the trace. Then restart with `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true` and open the GenAI view.
7. Add `LlmCostTelemetry.cs`: a `FinanceAssistant` meter with a token counter and a cost counter, fed by `.UseCostTracking()` middleware placed after each `.UseOpenTelemetry()`. Add `.AddMeter(ChatAgent.TelemetrySourceName)` to the meter provider.

---

## Step 1: Start the Aspire Dashboard

From any terminal:

```bash
docker run --rm -d --name aspire-dashboard -p 18888:18888 -p 4317:18889 -e ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true mcr.microsoft.com/dotnet/aspire-dashboard:13.6.1
```

Open `http://localhost:18888`. You should land on an empty Traces page.

Three things in that command are worth saying out loud:

**`-p 18888:18888` is the UI.** That's the page you'll show the room.

**`-p 4317:18889` is the OTLP endpoint.** Inside the container the dashboard listens for OTLP over gRPC on `18889`. We publish it on `4317`, the standard OTLP gRPC port, because that's where every OTel SDK sends by default. The app won't need an endpoint setting at all.

**`ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true` turns off auth.** The dashboard asks for a login token by default. This is fine on a laptop and wrong anywhere else. The orange "Endpoint is unsecured" banner is the dashboard agreeing with you.

> This is the standalone dashboard. You don't need an Aspire AppHost, and the app doesn't need to know it's talking to Aspire. It only knows it's sending OTLP. That's what lets you swap the backend later without touching the code.

We don't add it to `docker-compose.yml` on purpose. Students who never run this demo shouldn't pull another image on `docker compose up`.

---

## Step 2: Add the exporter package

Open `src/FinanceAssistant/FinanceAssistant.csproj`. In the `<ItemGroup>` with the other package references, add one line at the end:

```xml
<PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.20.1" />
```

That's the only new package. It brings the OpenTelemetry SDK with it as a dependency.

Notice what we're **not** adding. There's no "OpenTelemetry for OpenAI" or "OpenTelemetry for M.E.AI" instrumentation package. `Microsoft.Extensions.AI` already contains the instrumentation. It writes to .NET's own `System.Diagnostics.ActivitySource` and `System.Diagnostics.Metrics.Meter`, which OTel's SDK knows how to listen to. The package we add is only the listener and the wire format.

---

## Step 3: Let M.E.AI emit

Open `src/FinanceAssistant/ServiceCollectionExtensions.cs`. Two edits.

In `AddChatClient`, add `.UseOpenTelemetry()` after `.ConfigureOptions(...)`:

```csharp
                .GetChatClient(deployment)
                .AsIChatClient()
                .AsBuilder()
                .ConfigureOptions(o =>
                    o.Reasoning = new ReasoningOptions { Effort = reasoningEffort })
                // After ConfigureOptions, so the span records the request as it goes on the wire.
                .UseOpenTelemetry()
                .Build());
```

In `AddEmbeddingGenerator`, the generator has no builder yet. Give it one:

```csharp
                .GetEmbeddingClient(embeddingDeployment)
                .AsIEmbeddingGenerator()
                .AsBuilder()
                .UseOpenTelemetry()
                .Build());
```

**Order matters in a `ChatClientBuilder`.** The first `Use...` call is the outermost wrapper and the last one sits closest to the model. With `UseOpenTelemetry()` after `ConfigureOptions`, the telemetry client sees the request after the reasoning effort is applied. That's what actually went to Azure, and that's what you want on the span.

The embedding generator gets its own `UseOpenTelemetry()` because `SearchTransactionsTool` calls it on every search. Without it, a search looks free in the trace. It isn't. It's a network call to a different model.

Build now. It compiles, and nothing changes when you run it. M.E.AI creates its activities only when something is listening, and nothing is yet.

---

## Step 4: Ship it over OTLP

Open `src/FinanceAssistant/Program.cs`. Add four usings next to the others:

```csharp
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
```

Then, right after the usings and before the first `await using (var db = ...)`, add the providers:

```csharp
// M.E.AI's default name for the spans and metrics that UseOpenTelemetry() emits.
// "Experimental" because the GenAI semantic conventions are still in development.
const string MeaiTelemetrySourceName = "Experimental.Microsoft.Extensions.AI";

var resource = ResourceBuilder.CreateDefault().AddService("finance-assistant");

// Both exporters default to OTLP over gRPC on localhost:4317.
// Point them elsewhere with OTEL_EXPORTER_OTLP_ENDPOINT, no code change.
using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .SetResourceBuilder(resource)
    .AddSource(MeaiTelemetrySourceName)
    .AddSource(ChatAgent.TelemetrySourceName)
    .AddOtlpExporter()
    .Build();

using var meterProvider = Sdk.CreateMeterProviderBuilder()
    .SetResourceBuilder(resource)
    .AddMeter(MeaiTelemetrySourceName)
    // The default export interval is 60 seconds. Five keeps the dashboard live during a demo.
    .AddOtlpExporter((_, reader) =>
        reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 5_000)
    .Build();
```

`ChatAgent.TelemetrySourceName` doesn't exist yet. Step 5 adds it. If you want to run between steps, comment that one line out.

What each piece does:

**`AddSource` and `AddMeter` are opt-in filters.** The SDK only listens to the sources you name. `Experimental.Microsoft.Extensions.AI` is the name M.E.AI uses when you don't pass one to `UseOpenTelemetry(sourceName: ...)`. Forget it and you get an empty dashboard with no error. That's the most common OTel mistake, and it's worth a pause in the room.

**`AddService("finance-assistant")` is the resource.** It's how the backend tells this app apart from every other app sending to it. Every span and metric carries it.

**`using var` on both providers is the flush.** The exporters batch. When the REPL exits through `exit`, the `using` disposes the providers and the last batch goes out. Kill the process with Ctrl+C and the final few seconds of telemetry may never arrive.

**Traces and metrics are two pipelines.** Traces answer "what happened in this one request". Metrics answer "what's happening across all of them". Same source name, two different listeners.

### Run it once without the agent spans

With the `ChatAgent.TelemetrySourceName` line commented out, run the agent and ask:

```
> What did I spend on groceries in May 2026? Give me the total in EUR and USD.
```

Then open Traces in the dashboard. You'll see five or six separate traces for one question: a `chat chat` here, an `embeddings text-embedding-3-small` there, another `chat chat`. The second word is the deployment name, not the model. Each one is its own root.

That's the honest picture of a hand-written loop. M.E.AI knows when a model call starts and ends. It doesn't know those calls belong to one user turn, because the turn is our `for` loop, and our `for` loop doesn't tell anyone. Leave this on screen for a moment. Then fix it.

---

## Step 5: Own the loop's spans

Open `src/FinanceAssistant/ChatAgent.cs`. Four edits.

Add `using System.Diagnostics;` at the top. Then add a source at the top of the class:

```csharp
public class ChatAgent
{
    public const string TelemetrySourceName = "FinanceAssistant";

    // UseOpenTelemetry() traces each model call. The loop is ours, so the spans
    // that tie those calls into one turn are ours too.
    private static readonly ActivitySource Telemetry = new(TelemetrySourceName);
```

At the top of `RunTurnAsync`, start the turn span:

```csharp
    public async Task<string> RunTurnAsync(string input, CancellationToken ct = default)
    {
        // One span per turn. Every chat, embeddings and execute_tool span below becomes its child.
        using var agentActivity = Telemetry.StartActivity("invoke_agent FinanceAssistant");
        agentActivity?.SetTag("gen_ai.operation.name", "invoke_agent");
        agentActivity?.SetTag("gen_ai.agent.name", "FinanceAssistant");

        _store.AppendUserMessage(input);
```

Inside the `foreach (var call in toolCalls)` loop, right after the confirmation check and before `AIContent resultContent;`, start a tool span. Mark it as failed in the `catch`:

```csharp
                using var toolActivity = Telemetry.StartActivity($"execute_tool {call.Name}");
                toolActivity?.SetTag("gen_ai.operation.name", "execute_tool");
                toolActivity?.SetTag("gen_ai.tool.name", call.Name);
                toolActivity?.SetTag("gen_ai.tool.call.id", call.CallId);
                toolActivity?.SetTag("gen_ai.tool.type", "function");

                AIContent resultContent;
                try
                {
                    var result = await function.InvokeAsync(new AIFunctionArguments(call.Arguments), ct);
                    resultContent = new FunctionResultContent(call.CallId, result);
                }
                catch (Exception ex)
                {
                    toolActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    toolActivity?.SetTag("error.type", ex.GetType().FullName);

                    // Tool threw something the model didn't handle (or we didn't wrap at the tool boundary).
                    // Hand the model a structured error so it can recover or apologise.
                    resultContent = new FunctionResultContent(call.CallId, $"Tool error: {ex.Message}");
                }
```

Finally, mark the turn as failed when the iteration cap is hit. Right after the `Console.Error.WriteLine` near the end of the method:

```csharp
        agentActivity?.SetStatus(ActivityStatusCode.Error, "iteration cap reached");
```

Uncomment the `AddSource(ChatAgent.TelemetrySourceName)` line in `Program.cs` if you commented it out.

Why it's built this way:

**`Activity` is .NET's span.** OTel's .NET SDK didn't invent a new type. `ActivitySource.StartActivity` is in the base class library, which is why M.E.AI can emit telemetry without depending on OTel at all.

**Every `?.` is load-bearing.** `StartActivity` returns `null` when nobody listens to the source. That's why the instrumentation costs nothing in a process with no tracer provider.

**Parenting is automatic.** `Activity.Current` flows through `async` calls. Any span started while `agentActivity` is current, including M.E.AI's `chat` spans, becomes its child. We never pass a parent around.

**The names and tags come from the GenAI conventions.** `invoke_agent` and `execute_tool` aren't our invention. They're the span names the OTel GenAI agent conventions define, and they're the same names `FunctionInvokingChatClient` and Microsoft Agent Framework emit. Use them and every GenAI-aware backend draws your hand-written loop the same way it draws a framework one.

**The confirmation gate sits outside the tool span.** `ConfirmInteractive` runs before `StartActivity`. The time a human takes to type `yes` is still inside the turn, so it shows up as a gap in the waterfall rather than as a slow tool.

Build. Zero warnings, zero errors.

---

## Step 6: Run it

```bash
dotnet run --project src/FinanceAssistant
```

Ask the same question as before:

```
> What did I spend on groceries in May 2026? Give me the total in EUR and USD.
```

The console shows the loop calling `SearchTransactions`, `GetTransactions` and `Convert`, then an answer near €197.52 and $217.27. The exact path varies run to run. That variation is one of the things the trace makes visible.

### The trace

Open Traces. There's now one row per question, named `invoke_agent FinanceAssistant`. Click it:

```
invoke_agent FinanceAssistant
├─ chat chat
├─ execute_tool SearchTransactions
│  └─ embeddings text-embedding-3-small
├─ chat chat
├─ execute_tool GetTransactions
├─ chat chat
├─ execute_tool Convert
└─ chat chat
```

This is the agent loop from P3.01, drawn by a tool that's never seen our code. Point out three things:

- **The `chat` bars are most of the time.** Each one is one to two and a half seconds. The tools are milliseconds. The cost of an agent is the round trips to the model, not the work.
- **The `embeddings` span sits inside `SearchTransactions`.** The parent-child link came free with `Activity.Current`.
- **There's one more `chat` than there are tools.** The last call is the one that writes the answer.

Click any `chat` span and look at its attributes: `gen_ai.request.model` (the deployment, `chat`), `gen_ai.response.model` (the model that actually answered, like `gpt-5.6-luna-2026-07-09`), `gen_ai.response.finish_reasons`, `gen_ai.usage.input_tokens`, `gen_ai.usage.output_tokens`, `gen_ai.usage.cache_read.input_tokens`. Now click through the `chat` spans in order and read `gen_ai.usage.input_tokens` on each one. On our test run it went 660, 2,543, 3,770, 3,820.

**That's the agent loop's real cost.** Every iteration re-sends the whole history: the system prompt, the tool definitions, the question, every tool call and every tool result so far. Output stays at around 30 tokens a call. Input grows with every lap. One question cost more than 10,000 input tokens. P4.02's summarizing reducer suddenly has a number attached to it.

The `cache_read` attribute is the other half. After the first couple of calls, most of the input is a prefix Azure has already seen, so it's served from the prompt cache. On our test run the third call read 2,540 of its 3,770 input tokens from cache. Cached input tokens are billed at a discount. The trace shows whether the discount is actually applying.

### The content: off by default

Click the sparkle icon next to a `chat` span. The dashboard opens its GenAI view. The header shows the call's duration and token count, and the Tools tab lists all four tool definitions. Input and Output both say "No messages found". No system prompt, no question, no answer.

That's on purpose. Prompts and completions are the most sensitive data an agent touches, and in this app they're someone's bank transactions. M.E.AI leaves them out unless you opt in.

Exit the REPL with `exit`. Restart it with content capture turned on:

```bash
OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true dotnet run --project src/FinanceAssistant
```

On Windows PowerShell, set it first with `$env:OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT="true"`.

Ask the question again and open the GenAI view on the new trace. Now each `chat` span shows the system prompt, the user message, the assistant's tool calls, and the tool responses, in order. You can read exactly what the model saw when it decided to call `Convert`. When the agent gives a wrong answer, this is where you find out whether the model misread good data or the tool handed it bad data.

> The environment variable is an OTel convention, not an M.E.AI one. The code-level switch is `UseOpenTelemetry(configure: c => c.EnableSensitiveData = true)`. Prefer the environment variable. It keeps the decision with whoever runs the process, not whoever wrote it.

Say it plainly to the room: in production, this stays off, or it goes to a backend with the same access controls as the database.

### The metrics

Open Metrics and pick `finance-assistant` as the resource. Under `Experimental.Microsoft.Extensions.AI` there are two instruments:

- **`gen_ai.client.token.usage`** is a histogram of tokens per call. Filter `gen_ai.token.type` to `input`, then `output`, and the gap from the trace shows up as a chart.
- **`gen_ai.client.operation.duration`** is a histogram of seconds per call. Filter `gen_ai.operation.name` to `chat` versus `embeddings` to see which model you're waiting on.

Both carry `gen_ai.request.model` and `gen_ai.response.model`. The request model is the deployment name, `chat` on both paths. When P1.01's Free Tier and Tier 1 paths run different models, `gen_ai.response.model` is the dimension that splits them.

Traces are one request. Metrics are all of them. "Our P90 chat call takes two seconds and sends four thousand input tokens" is a sentence you can only say from here.

> **Histogram percentiles are bucket edges, not exact values.** The token histogram uses buckets that grow by powers of four: 1,024, 4,096, 16,384. A call with 5,800 input tokens lands in the 4,096 to 16,384 bucket, and the P99 line draws at 16k. Use the trace for exact numbers. Use the metric for trends.

### The human in the loop

Ask for a transfer and wait a few seconds before you type `yes`:

```
> Transfer 100 from Checking to Savings.
[agent] 'Transfer' requires confirmation.
        Arguments: fromAccount=Checking, toAccount=Savings, amount=100
        Type 'yes' to proceed: yes
```

Open the trace. The `invoke_agent` bar is long. The `chat` spans and `execute_tool Transfer` are short. Between the first `chat` and `execute_tool Transfer` there's an empty stretch. That's the human. P5.01's gate is visible in telemetry without a single line of code for it.

---

## Step 7: Put a price on it

The room now knows how many tokens a question takes. Nobody in the room thinks in tokens. They think in money. And the dashboard can't add up what we have: `gen_ai.client.token.usage` is a histogram, so it shows percentiles per call, never a running total.

Prices aren't something M.E.AI can know. They depend on your provider, your model, and your contract. That makes this the place to add a metric of our own: two counters, one for tokens and one for dollars, both split by model.

Create `src/FinanceAssistant/LlmCostTelemetry.cs`:

```csharp
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.AI;

namespace FinanceAssistant;

// M.E.AI's gen_ai.client.token.usage histogram answers "how big is a typical call".
// These counters answer "how much have I spent, and on which model".
public static class LlmCostTelemetry
{
    private const string CostTag = "finance_assistant.cost.usd";

    // USD per million tokens, from the price list in P1.01. Prices change, so check yours.
    private static readonly Dictionary<string, ModelPrice> Prices = new()
    {
        ["gpt-5.6-luna"] = new(Input: 0.20, CachedInput: 0.02, Output: 1.20),
        ["gpt-5.6-sol"] = new(Input: 4.00, CachedInput: 0.40, Output: 20.00),
        ["gpt-5-mini"] = new(Input: 0.25, CachedInput: 0.025, Output: 2.00),
        ["text-embedding-3-small"] = new(Input: 0.02, CachedInput: 0.02, Output: 0),
    };

    private static readonly Meter Meter = new(ChatAgent.TelemetrySourceName);

    private static readonly Counter<long> Tokens = Meter.CreateCounter<long>(
        "finance_assistant.llm.tokens",
        unit: "{token}",
        description: "Tokens billed, split into uncached input, cached input and output.");

    private static readonly Counter<double> Cost = Meter.CreateCounter<double>(
        "finance_assistant.llm.cost",
        unit: "{dollar}",
        description: "Estimated spend in USD, from token usage and list prices.");

    private static readonly HashSet<string> UnpricedModels = [];

    public static ChatClientBuilder UseCostTracking(this ChatClientBuilder builder) =>
        builder.Use(
            getResponseFunc: async (messages, options, inner, ct) =>
            {
                var response = await inner.GetResponseAsync(messages, options, ct);
                var model = response.ModelId ?? inner.GetService<ChatClientMetadata>()?.DefaultModelId;
                Record("chat", model, response.Usage);
                return response;
            },
            getStreamingResponseFunc: null);

    public static EmbeddingGeneratorBuilder<TInput, TEmbedding> UseCostTracking<TInput, TEmbedding>(
        this EmbeddingGeneratorBuilder<TInput, TEmbedding> builder)
        where TEmbedding : Embedding =>
        builder.Use(async (values, options, inner, ct) =>
        {
            var embeddings = await inner.GenerateAsync(values, options, ct);
            // Embedding responses don't name the model, so fall back to the deployment name.
            var model = inner.GetService<EmbeddingGeneratorMetadata>()?.DefaultModelId;
            Record("embeddings", model, embeddings.Usage);
            return embeddings;
        });

    private static void Record(string operation, string? model, UsageDetails? usage)
    {
        if (model is null || usage is null)
        {
            return;
        }

        // Cached tokens are part of the input count, but billed at a discount. Split them out
        // so the three token types don't overlap and add up to the total.
        var cachedInput = usage.CachedInputTokenCount ?? 0;
        var input = (usage.InputTokenCount ?? 0) - cachedInput;
        var output = usage.OutputTokenCount ?? 0;

        // The response names a dated snapshot, like gpt-5.6-luna-2026-07-09. Match on the prefix.
        var price = Prices.FirstOrDefault(p => model.StartsWith(p.Key, StringComparison.OrdinalIgnoreCase)).Value;
        if (price is null && UnpricedModels.Add(model))
        {
            Console.Error.WriteLine($"[cost] no price for '{model}'. Tokens are counted, cost is not.");
        }

        var cost = 0.0;
        cost += Add("input", input, price?.Input);
        cost += Add("cached_input", cachedInput, price?.CachedInput);
        cost += Add("output", output, price?.Output);

        if (price is null)
        {
            return;
        }

        // This runs inside UseOpenTelemetry(), so the current activity is this call's span.
        Activity.Current?.SetTag(CostTag, cost);

        // Add the call's cost to the turn, so each invoke_agent span says what the question cost.
        for (var turn = Activity.Current?.Parent; turn is not null; turn = turn.Parent)
        {
            if (turn.Source.Name == ChatAgent.TelemetrySourceName)
            {
                turn.SetTag(CostTag, (turn.GetTagItem(CostTag) as double? ?? 0) + cost);
                break;
            }
        }

        double Add(string tokenType, long count, double? pricePerMillion)
        {
            var tags = new TagList
            {
                { "gen_ai.operation.name", operation },
                { "gen_ai.response.model", model },
                { "finance_assistant.token.type", tokenType },
            };

            Tokens.Add(count, tags);
            if (pricePerMillion is null)
            {
                return 0;
            }

            var amount = count * pricePerMillion.Value / 1_000_000;
            Cost.Add(amount, tags);
            return amount;
        }
    }

    private sealed record ModelPrice(double Input, double CachedInput, double Output);
}
```

Then wire it in. In `ServiceCollectionExtensions.cs`, add `.UseCostTracking()` right after each `.UseOpenTelemetry()`:

```csharp
                .UseOpenTelemetry()
                // Inside the telemetry layer, so the cost lands on the chat span too.
                .UseCostTracking()
                .Build());
```

```csharp
                .AsBuilder()
                .UseOpenTelemetry()
                .UseCostTracking()
                .Build());
```

In `Program.cs`, the meter provider has to listen to the new meter. Add one line under the existing `AddMeter`:

```csharp
    .AddMeter(MeaiTelemetrySourceName)
    .AddMeter(ChatAgent.TelemetrySourceName)
```

The choices worth explaining:

**`builder.Use(...)` is middleware without a class.** It's the same pipeline `UseOpenTelemetry()` plugs into. We get the request, call `inner`, and read `response.Usage` on the way back. Any `IChatClient` in the app gets cost tracking the same way, including the summarizing reducer's, because the reducer uses the same registered client.

**It sits inside the telemetry layer on purpose.** `UseCostTracking()` comes after `UseOpenTelemetry()`, so it runs closer to the model. By the time it runs, the `chat` span already exists and is `Activity.Current`. That's how the cost lands on the span. Swap the two lines and the cost goes on whatever span is current at the time, which is the turn, not the call.

**Three token types that don't overlap.** M.E.AI counts cached tokens as part of the input. They're billed at a tenth of the price, so we split them out. `input`, `cached_input` and `output` add up to the total, and each one has its own price.

**Counters, not histograms.** A counter only goes up, and the dashboard draws it as a running total. That's the question "how much have I spent". The built-in histogram answers a different question, "how big is a typical call", and you want both.

**The unit is `{dollar}`, not `USD`.** The dashboard turns units into labels. `{token}` becomes "Tokens", `{dollar}` becomes "Dollars", and a bare `USD` comes out as "US Ds". The description says the currency.

**The price table is a guess you have to maintain.** These are list prices from P1.01. Your invoice can differ: regional pricing, provisioned throughput, batch discounts, credits. Treat this metric as an early warning, and reconcile it against Azure Cost Management.

Build, run, and ask a few questions. Ask more than one, so the turns have history behind them.

### What it shows

Open Metrics, pick `finance-assistant`, and expand the `FinanceAssistant` meter. Two new instruments:

- **`finance_assistant.llm.cost`** climbs with every call. Switch to Table for the exact running total. Filter `gen_ai.response.model` to split it per model, and `finance_assistant.token.type` to see whether you're paying for input or output.
- **`finance_assistant.llm.tokens`** is the same split in tokens.

Then open Traces and click any `invoke_agent` row. The span now carries `finance_assistant.cost.usd`, the cost of that one question. Every `chat` and `embeddings` span under it carries its own share.

On our test run, six questions on Luna cost about $0.008 in total. That's small, and it's worth saying why it still matters:

- **The expensive turns were the ones where the reducer ran.** The P4.02 summary is an extra model call with the whole history as input. Those turns cost $0.0027 and $0.0028, against $0.0005 for the first question.
- **Input is where the money goes.** Filter the cost metric by token type. Output tokens cost six times more per token, and input still wins, because the loop re-sends the history on every lap. On our run, uncached input was $0.0061 and output was $0.0012.
- **The cache is doing more than you'd think.** The same run read about 50,000 input tokens from cache for $0.001. At full price they'd have cost $0.010, more than the whole bill. Keep the system prompt and tool definitions stable and at the front, and the cache keeps working.
- **Embeddings round to zero.** A search embeds about seven tokens at $0.02 per million. The span is there and the cost is real, but it's not where you'd optimise.
- **Multiply it out.** A fraction of a cent per question, times turns per conversation, times users per day. Then remember that on Path A, Sol, the P5.02 judge, costs twenty times Luna per input token. That's the conversation the metric lets you have.

---

## Step 8: Diff what just happened

```bash
git diff --stat
```

Five files, a little under two hundred lines added:

- **`FinanceAssistant.csproj`**: one package, the OTLP exporter.
- **`ServiceCollectionExtensions.cs`**: two `UseOpenTelemetry()` calls and two `UseCostTracking()` calls. That's every model call instrumented and priced.
- **`Program.cs`**: two providers and one exporter each. That's the shipping.
- **`ChatAgent.cs`**: one `ActivitySource` and two spans. That's the part a framework would have given you.
- **`LlmCostTelemetry.cs`**: a meter, two counters, and a price table. That's the part only you know.

The split is the lesson. Instrumenting the model calls was two lines, because M.E.AI owns that boundary. Instrumenting the agent took more, because we own that boundary. Pricing it took the most, because nobody but you knows what you pay. After P6, when Demo 01 swaps `ChatAgent` for a `ChatClientAgent`, MAF has its own `UseOpenTelemetry()` on the agent builder that emits `invoke_agent` for you. You'd delete the spans we just wrote and keep everything else.

Nothing in the app knows it's talking to the Aspire Dashboard. It only sends OTLP. Instrumentation is the expensive part, and it's the part you don't redo when you change your mind about the backend. App Insights, Grafana, Honeycomb, Datadog: each is an OTLP endpoint or an exporter package away.

Clean up when you're done:

```bash
docker stop aspire-dashboard
```

---

## Troubleshooting

### The dashboard stays empty

Check three things in order. Is the container up (`docker ps`, look for `aspire-dashboard`)? Is the source name in `AddSource` spelled exactly `Experimental.Microsoft.Extensions.AI`? Did you `exit` the REPL rather than Ctrl+C it? The app doesn't fail when the exporter can't connect. The spans are dropped and the agent answers as normal, which is the right behaviour for telemetry and an awkward one in a demo.

### `docker run` fails with "port is already allocated"

Something else holds `4317` or `18888`. Often it's a previous `aspire-dashboard` container or a local OTel Collector. Run `docker ps` and stop it. If you can't free `4317`, publish the dashboard on another port, for example `-p 4320:18889`, and run the agent with `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4320`.

### One question shows up as several separate traces

The agent spans aren't being recorded. Either `AddSource(ChatAgent.TelemetrySourceName)` is still commented out in `Program.cs`, or `StartActivity` is being called on a source whose name doesn't match. That's exactly what Step 4's run looked like.

### Traces arrive, metrics don't

Metrics export on a timer, not per call. Wait five seconds and refresh. If you removed the `ExportIntervalMilliseconds` override, the SDK default is 60 seconds. Also check that the Metrics page has `finance-assistant` selected as the resource. It starts on `(None)`.

### The GenAI view shows no messages

Content capture is off. That's the default. Restart with `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true`. The variable is read when the chat client is built, so setting it in a running process does nothing.

### Each run shows up as `finance-assistant-<hex>`

That's the service instance ID. The SDK generates a new one per process, and the dashboard lists each run as a replica. On the Metrics page, pick the `finance-assistant (application)` entry to see all runs together.

### The console prints `[cost] no price for '...'`

The model name didn't match any key in the price table. For chat calls the name comes from the response, like `gpt-5.6-luna-2026-07-09`, and matches on the prefix. For embeddings it's your deployment name, because embedding responses don't name the model. If you gave a deployment a custom name, add that name to `Prices`. Tokens are still counted either way.

### `finance_assistant.llm.cost` is missing from the Metrics page

The meter provider isn't listening to the `FinanceAssistant` meter. Check for `.AddMeter(ChatAgent.TelemetrySourceName)` in `Program.cs`. `AddSource` on the tracer provider doesn't cover it. Traces and metrics opt in separately.

### `gen_ai.usage.reasoning.output_tokens` is always 0

The model isn't spending tokens on reasoning for questions this simple at the reasoning effort the workshop sets. A higher `AzureOpenAI:ReasoningEffort` or a harder question can change that. Reasoning tokens are billed as output, so the attribute is worth watching once they appear.

---

## You can now

Instrument any `IChatClient` or `IEmbeddingGenerator` with one `UseOpenTelemetry()` call, export the result over OTLP to whichever backend your team runs, and add the agent-level spans your own loop owns. You can read a single turn as a waterfall, find the call that cost the most tokens, and watch latency and token usage across every call over time. You can also add your own metrics next to the built-in ones, and turn tokens into an estimate of what each question, each model, and the whole session costs.

---

## Summary

You've changed:

- **`FinanceAssistant.csproj`**: one new `PackageReference` for `OpenTelemetry.Exporter.OpenTelemetryProtocol` (`1.20.1`).
- **`ServiceCollectionExtensions.cs`**: `UseOpenTelemetry()` then `UseCostTracking()` on the chat client and on the embedding generator.
- **`Program.cs`**: a tracer provider and a meter provider, both listening to `Experimental.Microsoft.Extensions.AI` and `FinanceAssistant`, both exporting OTLP to `localhost:4317` by default. Metrics export every five seconds.
- **`ChatAgent.cs`**: a `FinanceAssistant` `ActivitySource`, an `invoke_agent` span per turn, an `execute_tool` span per tool call, and error status on tool exceptions and on the iteration cap.
- **`LlmCostTelemetry.cs`**: new. A `FinanceAssistant` meter with `finance_assistant.llm.tokens` and `finance_assistant.llm.cost` counters, a price table, and the middleware that feeds them and tags each span with its cost.

What stayed the same:

- The tools, the system prompt, the confirmation gate, the reducer, and the REPL.
- The agent's behaviour. With no backend running, it answers exactly as it did on `p5-01-end`.

What we deliberately left out:

- Logs. OTel has a third signal, and the dashboard has a page for it. The REPL's `Console.WriteLine` lines aren't structured logs, and converting them is a different lesson.
- HTTP and database spans. `OpenTelemetry.Instrumentation.Http` would add the raw `POST` under each `chat` span, and Npgsql has its own instrumentation. They're useful in production and noise in a demo about LLM calls.

---

## Additional Resources

- [OpenTelemetry for GenAI: observing AI agents](https://opentelemetry.io/blog/2026/genai-observability/): the OTel blog post this demo follows, using the same dashboard.
- [GenAI semantic conventions](https://opentelemetry.io/docs/specs/semconv/gen-ai/): the span names, attributes, and events. M.E.AI `10.9.0` implements v1.41.
- [GenAI metrics](https://opentelemetry.io/docs/specs/semconv/gen-ai/gen-ai-metrics/): `gen_ai.client.token.usage`, `gen_ai.client.operation.duration`, and their bucket advice.
- [GenAI agent spans](https://opentelemetry.io/docs/specs/semconv/gen-ai/gen-ai-agent-spans/): `invoke_agent` and `execute_tool`.
- [Standalone Aspire Dashboard](https://aspire.dev/dashboard/standalone/): configuration, auth, and the full list of ports.
- [OTel .NET exporters](https://opentelemetry.io/docs/languages/dotnet/exporters/): OTLP options and the `OTEL_EXPORTER_OTLP_*` environment variables.