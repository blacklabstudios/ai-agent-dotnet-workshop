# P2.02 - GetTransactions and SearchTransactions

> Pillar 2, Part 2. Individual.

## Mission

Add two real tools backed by your transactions database. `GetTransactions` answers date-range queries against a deliberately strict parser. `SearchTransactions` answers fuzzy questions through embeddings stored in pgvector, ordered by cosine distance at the SQL level.

By the end, the agent has three tools (Convert, Get, Search) and picks the right one based on how the question is phrased.

**Learning Objectives**:

- Return structured errors at the tool boundary
- Generate vectors with `IEmbeddingGenerator<string, Embedding<float>>`
- Search similar transactions in Postgres with pgvector
- Help the agent choose between tools through precise `[Description]` text

---

## Prerequisites

- P2.01 finished. ConvertCurrency tool runs against `gpt-5.6-luna`.
- The starter is on Postgres with the `pgvector` extension enabled. Image: `pgvector/pgvector:pg18` or equivalent. `docker compose up -d` from the repo root brings it up.
- `FinanceAssistant.csproj` references `Pgvector.EntityFrameworkCore` (`0.3.0` or later).
- `Transaction` entity has a nullable `Pgvector.Vector` property called `Embedding`.
- `FinanceDbContext.OnModelCreating` calls `modelBuilder.HasPostgresExtension("vector")`, sets the column type to `vector(1536)` (the dimension `text-embedding-3-small` returns), and adds an HNSW index on the column with `vector_cosine_ops`.
- `FinanceDbContext.OnConfiguring` uses the Npgsql provider and the options call `o.UseVector()` so EF knows about the vector type.
- Postgres connection string is the `DefaultConnectionString` constant in `FinanceDbContext`, and it matches the credentials in `docker-compose.yml`. There is no `appsettings.json` and no user-secrets entry for it.

> **Why HNSW.** Without an index, pgvector does a sequential scan and computes cosine distance against every row. Fine for 400 transactions. Painful for 400,000. HNSW (Hierarchical Navigable Small World) is an approximate-nearest-neighbour graph that returns close-enough matches in logarithmic time. The query syntax doesn't change, which is the point: you build the index once and the same LINQ keeps working.
>
> Don't expect to see it used today. At 400 rows the planner will correctly pick a sequential scan, and `EXPLAIN ANALYZE` on the search query will say `Seq Scan` no matter how right your index is. You're building it for a table this workshop never grows to.

---

## What we're solving

The model can chat and convert currencies. It cannot answer "How much did I spend on restaurants last month?" because it cannot see your transactions.

`GetTransactions` answers questions like "What did I spend on 2026-03-15?" or "Show me transactions between 2026-01-01 and 2026-01-31". It takes a date or a range, queries the DB, returns the rows.

`SearchTransactions` answers questions like "Find anything about coffee" or "Subscriptions I might want to cancel". It embeds the query, asks pgvector for the closest matches by cosine distance, and returns them. The vectors stay in the database. Only the query embedding crosses the wire each time.

Two patterns matter more than the queries:

1. **Tools don't let exceptions escape.** `TransactionsService.GetTransactions` parses strictly and throws `FormatException` on anything that isn't ISO 8601. If that throw leaves your tool, the function-invocation pipeline hands the model a generic tool failure, and a generic failure is one the model can't act on. Catch it at the boundary and return a structured result instead. A tool's job is to always hand back something readable, including when the readable thing is "you asked for that wrong".

2. **Multi-tool disambiguation.** With three tools registered (Convert, Get, Search), the agent has to pick the right one for each question. That decision is driven entirely by your `[Description]` text. Same lesson as P2.01, applied across multiple tools.

> The strict parser is a teaching artefact. In production you'd want the parser itself to handle natural language (relative dates, named months, locale-aware formats). The point of the exercise is that you can't always trust your own services. Tools sit at the boundary between the model and your code, and the boundary is the right place to convert exceptions into something the model can read.

---

## Where embeddings put things

Before you register anything, get a picture of what an embedding actually is. The code below is that picture, written down.

Think of a map.

Every transaction in your database gets a pin. Not by street address, by meaning. "Starbucks, flat white" and "Kaffebrenneriet, oat latte" land next to each other because they mean nearly the same thing. "Ryanair, Oslo to Lisbon" lands somewhere else entirely.

Searching is dropping one more pin. The user asks "find anything about coffee", you drop a pin for that phrase, and you return the transactions nearest to it. You never match the word "coffee" against anything. The word "coffee" doesn't have to appear in the row at all.

That's the whole trick, and it's the reason `SearchTransactions` can answer questions `GetTransactions` can't.

### How the model learns where to put things

Three steps, and none of them involve anybody writing rules.

1. It reads a mountain of text.
2. It notices which words show up near each other, again and again.
3. Words that keep appearing in similar company get placed close together.

That's it. Nobody told it a latte is a coffee. It read enough sentences where "latte" and "coffee" sat near each other to put them near each other, and enough where "latte" and "boarding pass" didn't.

The clearest demonstration is outside finance. "How do I fix a leaky tap?" lands next to "How do I repair a dripping faucet" and nowhere near "How do I bake bread". Not one meaningful word is shared between them. They land together anyway, because the company those words keep is the same.

### Where the analogy breaks

Two places, and both are worth knowing before you read the code.

**The map is flat. This one isn't.** `text-embedding-3-small` returns 1536 numbers per piece of text, and those numbers are the coordinates. You can't picture 1536 dimensions and you don't need to. The maths for "how far apart are these two points" works the same at 1536 as it does at 2.

**Distance here is an angle, not a walk.** Cosine distance measures the angle between two vectors, not the metres between two pins. That's why a three-word transaction and a long paragraph about the same thing still land close. Length pushes a point further from the origin. The direction it points mostly survives.

Everything below is that picture in code. Step 2 registers the thing that drops pins. Step 3 drops one for every transaction you have and stores it in the `vector(1536)` column. Step 5 drops one for the query and asks Postgres for the nearest. Today Postgres will answer that by measuring the distance to all 400 pins, because at 400 pins that's the fastest thing to do. The HNSW index from your prerequisites is what stops it having to, on the day the map has 400,000.

---

## If you're comfortable, do this

Use this list if you want the route first. The full steps explain the choices and help you recover when a step fails.

1. Deploy `text-embedding-3-small` in Azure AI Foundry. Add a fourth user secret `AzureOpenAI:EmbeddingDeployment`.
2. Add an `AddEmbeddingGenerator` extension method in `ServiceCollectionExtensions.cs` that registers `IEmbeddingGenerator<string, Embedding<float>>`.
3. In `Program.cs`, embed any transaction whose `Embedding` column is `null` and persist the result.
4. Create `Tools/GetTransactionsTool.cs` (with the `FormatException` catch at the tool boundary).
5. Create `Tools/SearchTransactionsTool.cs` that embeds the query and orders by pgvector's `CosineDistance`.
6. Register both new tools in `ChatOptions.Tools` alongside `ConvertCurrency`. Run. Try a single-date question, a range question, a search question and a conversion. Confirm each lands on the right tool.

---

## Step 1: Deploy the embeddings model and add the secret

### 1.1: Deploy `text-embedding-3-small`

Open Azure AI Foundry from your resource. Same place you deployed `gpt-5.6-luna` in P1.01.

Click "Deploy model", then "Deploy base model". Pick `text-embedding-3-small`.

- **Deployment name**: `text-embedding-3-small`. Use exactly this name.
- **Deployment type**: Standard.
- Leave the rest at defaults.

Click "Deploy" and wait for the green check.

### 1.2: Add the user secret

```bash
cd src/FinanceAssistant
dotnet user-secrets set "AzureOpenAI:EmbeddingDeployment" "text-embedding-3-small"
```

Verify with `dotnet user-secrets list`. You should now see four keys.

---

## Step 2: Register the embedding generator

Open `src/FinanceAssistant/ServiceCollectionExtensions.cs`. Add a second extension method below `AddChatClient`:

```csharp
public static IServiceCollection AddEmbeddingGenerator(this IServiceCollection services, IConfiguration config)
{
    var endpoint = config["AzureOpenAI:Endpoint"]
        ?? throw new InvalidOperationException("Missing AzureOpenAI:Endpoint.");
    var apiKey = config["AzureOpenAI:ApiKey"]
        ?? throw new InvalidOperationException("Missing AzureOpenAI:ApiKey.");
    var embeddingDeployment = config["AzureOpenAI:EmbeddingDeployment"]
        ?? throw new InvalidOperationException("Missing AzureOpenAI:EmbeddingDeployment.");

    var apiBase = new UriBuilder(endpoint) { Path = "openai/v1/" }.Uri;

    return services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(_ =>
        new OpenAIClient(
                new ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = apiBase })
            .GetEmbeddingClient(embeddingDeployment)
            .AsIEmbeddingGenerator());
}
```

> The embedding client uses the same Azure v1-compatible endpoint as the chat client. Same auth, same URL pattern, different deployment name. Two clients, one resource, one key.

Notice there's no `IEmbeddingsIndex` registration. We don't need an in-memory index. pgvector is the index.

---

## Step 3: Embed transactions on first run

Three discrete edits to `Program.cs`.

### 3.1: Add usings

At the top of `Program.cs`, alongside the existing `using` lines, add:

```csharp
using Microsoft.EntityFrameworkCore;
using Pgvector;
```

### 3.2: Register the embedding generator

Find the `services.AddChatClient(config);` line. Add a sibling line right after:

```csharp
services.AddEmbeddingGenerator(config);
```

### 3.3: Embed any transactions missing an embedding

Right after the `var chatClient = provider.GetRequiredService<IChatClient>();` line, and above the `var convertCurrency = new ConvertCurrencyTool();` block you added in P2.01, add this. It has to sit above that block: Step 6 passes `embedder` to a constructor there, so the declaration needs to come first.

```csharp
var embedder = provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

await using (var db = new FinanceDbContext())
{
    var unembedded = await db.Transactions
        .Where(t => t.Embedding == null)
        .ToListAsync();

    if (unembedded.Count > 0)
    {
        Console.WriteLine($"Embedding {unembedded.Count} transactions...");
        var texts = unembedded.Select(t => $"{t.Merchant} {t.Description}").ToList();
        var embeddings = await embedder.GenerateAsync(texts);
        for (int i = 0; i < unembedded.Count; i++)
        {
            unembedded[i].Embedding = new Vector(embeddings[i].Vector.ToArray());
        }
        await db.SaveChangesAsync();
        Console.WriteLine($"Embedded {unembedded.Count} transactions.");
    }
}
```

The `Where(t => t.Embedding == null)` filter means this block only does work the first time. Once every transaction has an embedding the query matches nothing, `unembedded` comes back empty, and the block falls straight through without calling the embeddings API.

> **Embedding generation in production.** Doing this on REPL startup is a workshop convenience. In a real system, embedding generation is its own infrastructure. Pick whichever shape fits your stack: a recurring background job that processes new and updated rows from a queue, a domain-event handler that fires whenever a transaction is inserted or its searchable text changes, or a sync hook in your write path. The principle is "embed once per row when its searchable text changes, persist the vector, never compute again." The startup hook in this exercise is the smallest version of that pipeline.

---

## Step 4: Create GetTransactionsTool

Create `src/FinanceAssistant/Tools/GetTransactionsTool.cs`:

```csharp
using System.ComponentModel;
using FinanceAssistant.Data;
using FinanceAssistant.Services;

namespace FinanceAssistant.Tools;

public class GetTransactionsTool
{
    [Description("List transactions in a given date range. The dateExpression must be ISO 8601: a single date '2026-05-06' or a range '2026-01-01..2026-01-31'. Natural language like 'yesterday' or 'last month' is not supported. Convert any relative date to ISO 8601 first.")]
    public async Task<object> GetTransactions(
        [Description("ISO 8601 date or range, e.g. '2026-01-15' for one day or '2026-01-01..2026-01-31' for a range.")] string dateExpression,
        CancellationToken ct = default)
    {
        try
        {
            await using var db = new FinanceDbContext();
            var service = new TransactionsService(db);
            var transactions = await service.GetTransactions(dateExpression, ct);
            return new { transactions };
        }
        catch (FormatException ex)
        {
            // The service throws on natural-language dates by design.
            // We turn the throw into a structured error the model can reason about.
            return new
            {
                error = "invalid_date",
                hint = "Use ISO 8601: YYYY-MM-DD for a single date, or YYYY-MM-DD..YYYY-MM-DD for a range.",
                message = ex.Message
            };
        }
    }
}
```

> The `try`/`catch` is the entire lesson, and if you've written the `[Description]` well you'll probably never watch it fire. That's not a reason to skip it.
>
> The description above is explicit that the argument must be ISO 8601, so a capable model converts "March 2026" or "last month" into a range itself and calls you with something valid. The description is your first line of defence, and it's the cheap one. The `catch` is the second, for the day the first isn't enough: a smaller model, a colder prompt, a phrasing nobody tested. Take the `catch` out and that day ends with `FormatException` reaching the function-invocation pipeline, the model receiving a generic tool failure, and the user getting an apology instead of data: "I couldn't retrieve your transactions, so I can't calculate the total." Leave it in and the model gets a `hint` field naming the format, which is something it can retry against.

---

## Step 5: Create SearchTransactionsTool

Create `src/FinanceAssistant/Tools/SearchTransactionsTool.cs`:

```csharp
using System.ComponentModel;
using FinanceAssistant.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace FinanceAssistant.Tools;

public class SearchTransactionsTool
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;

    public SearchTransactionsTool(IEmbeddingGenerator<string, Embedding<float>> embedder)
    {
        _embedder = embedder;
    }

    [Description("Search transactions by free-text similarity over merchant and description fields. Returns the top K matching transactions ordered by relevance. Use this when the user asks about purchases by topic, theme, or fuzzy description, like 'coffee shops', 'subscriptions I might cancel', or 'flights last quarter'.")]
    public async Task<object> SearchTransactions(
        [Description("Free-text query. Examples: 'coffee shops in december', 'subscription cancellations', 'restaurants in Lisbon'.")] string query,
        [Description("How many top matches to return. Default 5. Maximum 20.")] int topK = 5,
        CancellationToken ct = default)
    {
        var queryEmbedding = await _embedder.GenerateAsync(query, cancellationToken: ct);
        var queryVector = new Vector(queryEmbedding.Vector.ToArray());
        var limit = Math.Clamp(topK, 1, 20);

        await using var db = new FinanceDbContext();
        var matches = await db.Transactions
            .Where(t => t.Embedding != null)
            .OrderBy(t => t.Embedding!.CosineDistance(queryVector))
            .Take(limit)
            .Select(t => new
            {
                t.Id,
                t.Date,
                t.Amount,
                t.Merchant,
                t.Category,
                t.Description,
                Distance = t.Embedding!.CosineDistance(queryVector)
            })
            .ToListAsync(ct);

        return new { matches };
    }
}
```

> `CosineDistance` translates to pgvector's `<=>` operator. Postgres sorts by distance, applies `LIMIT`, and returns the top K rows. No vectors get pulled into the .NET process for ranking. That's the pgvector pitch in one sentence: "let the database do the work."

---

## Step 6: Register both tools and run

One edit to `Program.cs`, then run.

### 6.1: Instantiate the tools and add them to ChatOptions

Find the `var chatOptions = new ChatOptions { ... };` block from P2.01. Update it to register all three tools:

```csharp
var convertCurrency = new ConvertCurrencyTool();
var getTransactions = new GetTransactionsTool();
var searchTransactions = new SearchTransactionsTool(embedder);

var chatOptions = new ChatOptions
{
    Tools =
    [
        AIFunctionFactory.Create(convertCurrency.Convert),
        AIFunctionFactory.Create(getTransactions.GetTransactions),
        AIFunctionFactory.Create(searchTransactions.SearchTransactions)
    ]
};
```

> `searchTransactions` takes `embedder`, the variable you declared in Step 3.3. If the compiler tells you it doesn't exist here, your embedding block landed below this one. Move it up.

### 6.2: Run

From the repo root:

```bash
dotnet run --project src/FinanceAssistant
```

On first run you'll see the embedding pass: `Embedding 400 transactions...` followed by `Embedded 400 transactions.`. On subsequent runs that block is silent (everything has an embedding already).

Try four prompts in turn. Each one tests something different:

- `Show me transactions on 2026-03-15` (Get tool, single ISO date)
- `Show me transactions between 2026-01-01 and 2026-01-31` (Get tool, range syntax)
- `Find anything about coffee` (Search tool, fuzzy query)
- `Convert 100 EUR to USD` (Convert tool from P2.01, still works)

The second one is worth a second look. Nothing in the conversation ever tells the model about the `..` range format. It appears in exactly one place, your `[Description]`, and the model turns an English sentence built around the word "between" into `2026-01-01..2026-01-31`. That's the description earning its keep.

If the agent picks the wrong tool for any of these, your `[Description]` text isn't doing enough work. Sharpen it.

---

## Troubleshooting

### `Missing AzureOpenAI:EmbeddingDeployment` thrown at startup

You haven't set the fourth user secret. From `src/FinanceAssistant/`:

```bash
dotnet user-secrets set "AzureOpenAI:EmbeddingDeployment" "text-embedding-3-small"
```

### `extension "vector" does not exist` from Postgres

The pgvector extension isn't enabled in your database. Connect to the Postgres instance and run:

```sql
CREATE EXTENSION IF NOT EXISTS vector;
```

If you're using the `pgvector/pgvector` Docker image, this should be available out of the box. If you swapped to a stock `postgres` image, install pgvector or switch images.

### `CosineDistance` is not found

You're missing the `Pgvector.EntityFrameworkCore` package or the `using Pgvector.EntityFrameworkCore;` line at the top of `SearchTransactionsTool.cs`. Both are needed for the EF translation to work.

### `AsIEmbeddingGenerator()` is not found

Same package as `AsIChatClient` (`Microsoft.Extensions.AI.OpenAI`). The using is `using Microsoft.Extensions.AI;`. Already there from P1.02.

### Search returns matches but the agent ignores them

The model decided not to surface the search results. Two checks:

1. The system prompt in `Prompts/SystemPrompt.md`. The shipped one says "Be concise", which is about length, not about where the answer comes from. If you've edited it to lean on the model's own knowledge, it may prefer its training data over the tool output. Add a line like "Prefer information from tool results over your own knowledge."
2. The tool description. If it doesn't make clear that the tool returns the actual transactions, the model may discount them.

### Agent calls `GetTransactions` for fuzzy questions like "coffee shops"

The model is reading the descriptions and the date-range tool is winning ambiguous calls. Sharpen `SearchTransactions` to say "use this when the user asks about purchases by topic, theme, or fuzzy description". Sharpen `GetTransactions` to say "only use this when an ISO 8601 date or range is specified".

### Agent calls `SearchTransactions` for date questions like "What did I spend on 2026-01-15?"

The reverse problem. Same fix in the opposite direction. Strong descriptions matter both ways.

---

## You can now

Ask three different kinds of questions and watch the agent route to the right tool:

- "Show me transactions on 2026-03-15" hits `GetTransactions`.
- "Find anything about coffee" hits `SearchTransactions`. Postgres ranks the rows by cosine distance and returns the top K.
- "Convert 100 EUR to USD" hits `ConvertCurrency` from P2.01.

And `GetTransactions` hands back a structured result rather than throwing, so a malformed date reaches the model as something it can read and retry against instead of an opaque failure it can only apologise for.

---

## Summary

You've added:

- **`AddEmbeddingGenerator`**: a second extension on `IServiceCollection` that registers `IEmbeddingGenerator`.
- **A startup embedder**: every transaction's `Merchant + Description` is embedded once when its row first lands in the DB, persisted as a `vector(1536)` column.
- **`Tools/GetTransactionsTool.cs`**: date-range query, with the strict parser's `FormatException` converted into a structured result at the tool boundary.
- **`Tools/SearchTransactionsTool.cs`**: free-text similarity search ordered by pgvector's cosine distance.
- **A three-tool agent**: Convert, Get, Search, all picked by the model based on the question.

---

## What's next

P3.01 is where the agent loop becomes visible. With three tools, the model sometimes wants to call multiple in sequence (e.g. "find coffee shops, then total what I spent there last quarter"). You'll write a `ChatAgent` that handles multi-turn tool calls, plus an iteration cap so a misbehaving model can't burn through your token budget.

---

## Additional Resources

- [Microsoft.Extensions.AI tool calling](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)
- [pgvector on GitHub](https://github.com/pgvector/pgvector)
- [Pgvector.EntityFrameworkCore on NuGet](https://www.nuget.org/packages/Pgvector.EntityFrameworkCore)
