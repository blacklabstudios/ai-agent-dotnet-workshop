# P2.B01 - Import a CSV Statement

> Pillar 2 bonus. Optional. Pick up after P2.02.

## Mission

Add an `ImportStatement` tool that loads a bank-statement CSV into the transactions database. Ask the agent to import `/Users/me/Downloads/statement.csv`. It reads the file, inserts the rows, and reports the result.

You can then add data without changing the seeder or restarting the app.

**Learning Objectives**:

- Design a tool that writes data
- Keep one bad row from stopping an import
- Skip duplicates at the tool boundary
- Return enough context for the agent to give a useful follow-up

---

## Prerequisites

- P2.02 finished. The agent has `GetTransactions` and `SearchTransactions` working.
- The repo already references `CsvHelper` (used by `TransactionsSeeder`). No new packages.
- A CSV file on disk you can point the tool at. Step 3 has a two-row file to copy. Bank exports work if they match the canonical schema below.

> **Do not smoke-test with `scaffolding/transactions.csv`.** Your database was seeded from that exact file at P2.02, so every one of its 400 rows hashes as a duplicate. The import reports `importedCount: 0` and writes nothing, which looks exactly like a broken tool. It's an excellent test of the duplicate check once the tool works. It's a terrible first test.

---

## What we're solving

`TransactionsSeeder` runs once when the database is first created and reads `scaffolding/transactions.csv`. After that, the only way to add transactions is to drop the DB volume and re-seed. That's fine for the workshop. It isn't fine if you want to play with your own statement after the workshop, or load a second month, or stress-test the search tool with more data.

Give the agent a function that takes a file path, parses the CSV, inserts the rows, and returns counts. The choices that matter are at the tool boundary, not in the SQL.

> **Why this is a tool and not a CLI flag.** Both work. A CLI flag is fine for one-shot batch loads. A tool is the right choice when you want the agent itself to handle the operation as part of a conversation: "import this, then show me my top categories last month". The agent chains the two calls. That conversational shape is the through-line of the workshop, and `ImportStatement` is the smallest possible "tool with side effects" example.

Three patterns to notice:

1. **Tool descriptions for write operations.** Read tools (`GetTransactions`, `SearchTransactions`) are safe to call freely. A write tool changes the database. The description should make that explicit so the model thinks twice before guessing arguments. We won't wire it through the confirmation gate from P5.01 here, but the description hint is the first line of defence.

2. **Partial success.** A 1000-row CSV with two malformed rows should import 998 rows and tell the agent which two failed. Throwing on the first bad row is the easy implementation and the wrong one. The agent can't act on "FormatException at row 412". It can act on "I imported 998 of 1000 rows. Row 412 and row 700 had unparseable dates."

3. **Idempotency by content hash.** If a user imports the same file twice, the second run should be a no-op, not a duplicate. We hash `(Date, Amount, Merchant, Description)` and skip rows that already exist.

---

## Canonical CSV format

The tool accepts exactly these five columns. The names have to match. The order doesn't, because CsvHelper maps on header name rather than position:

```csv
Date,Amount,Merchant,Category,Description
```

- `Date`: ISO 8601, `YYYY-MM-DD`. No other formats. `2026-01-15`.
- `Amount`: decimal. Negative for expenses, positive for income. `-42.50` or `1500.00`. No currency symbols, no thousands separators.
- `Merchant`: free text. Max 200 chars.
- `Category`: free text. Max 100 chars. Use whatever taxonomy you like, but stay consistent so the analysis tool in P2.B02 can map them.
- `Description`: free text. Max 2000 chars.

> **Why one format and not configurable mapping.** Real bank CSVs are messy. Different banks use different column names, different date formats, different sign conventions for debits and credits. A configurable mapper is the right answer for a production importer and the wrong answer for a workshop bonus. We document one schema and ask you to open your bank export in a spreadsheet and reshape it once before running the import. The lesson is the tool shape, not the CSV dialect zoo.

The seed file at `scaffolding/transactions.csv` already matches this schema. Use it as a reference if you need an example.

---

## If you're comfortable, do this

Use this list if you want the route first. The full steps explain the choices and help you recover when a step fails.

1. Create `src/FinanceAssistant/Tools/ImportStatementTool.cs`. Give it a file-path parameter and an optional `skipDuplicates` flag. Return `{ error = "file_not_found", hint, path }` before you open a missing file. Configure `CsvHelper` like `TransactionsSeeder`, but use `ReadAsync()` and `GetRecord<T>()` in a manual loop. Read the header before the loop.
2. Parse each row in its own `try`/`catch`. Catch `CsvHelperException` and `FormatException`. Let IO and database failures stop the import. Track `imported`, `skipped`, and `errors`. Hash `(Date, Amount, Merchant, Description)` to find duplicates, including repeated rows in one file.
3. Return `importedCount`, `skippedCount`, and `errorCount`. Return small samples of `skipped` and `errors`. Add a `note` when imported rows need an application restart before `SearchTransactions` can find them.
4. Register the tool alongside the others. On P2.02 that list is in `Program.cs`. On P5.02 or later it moved to `AgentToolset.cs`.
5. Save this as `/tmp/test-import.csv`, run, type `import the statement at /tmp/test-import.csv`, and confirm the agent calls the tool and reports the counts.

   ```csv
   Date,Amount,Merchant,Category,Description
   2026-05-01,-12.50,Test Coffee,Restaurants,Bonus import smoke test
   2026-05-02,-9.99,Test Sub,Subscriptions,Bonus import smoke test
   ```

> **Do not read with `GetRecords<T>()`.** It's what `TransactionsSeeder` uses and it's the wrong choice here. `GetRecords<T>()` returns a lazy enumerator. The first unparseable row throws, and once you've caught that exception the enumerator is finished: the next `MoveNext()` returns `false` without raising anything. Every remaining row is dropped in silence, so a 1000-row file with a bad row at 412 reports a tidy "411 imported" and you never learn about the other 588. The manual `ReadAsync()` loop in Step 1 is what makes per-row recovery possible.

---

## Step 1: Create ImportStatementTool

Create `src/FinanceAssistant/Tools/ImportStatementTool.cs`:

```csharp
using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.Configuration.Attributes;
using FinanceAssistant.Data;
using FinanceAssistant.Models;
using Microsoft.EntityFrameworkCore;

namespace FinanceAssistant.Tools;

public class ImportStatementTool
{
    [Description(
        "Import a bank statement CSV into the transactions database. " +
        "This tool MODIFIES the database. Only call it when the user explicitly asks to import, load, or upload a statement file. " +
        "The CSV must have headers Date,Amount,Merchant,Category,Description with Date in YYYY-MM-DD format and Amount as a decimal (negative for expenses).")]
    public async Task<object> ImportStatement(
        [Description("Absolute path to the CSV file on disk. Example: '/Users/me/Downloads/statement.csv'.")] string filePath,
        [Description("Skip rows that already exist in the database, and repeats within the file itself, matched by Date+Amount+Merchant+Description. Default true.")] bool skipDuplicates = true,
        CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
        {
            return new
            {
                error = "file_not_found",
                hint = "Pass an absolute path. Tilde (~) is not expanded.",
                path = filePath
            };
        }

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            TrimOptions = TrimOptions.Trim
        };

        var imported = new List<object>();
        var skipped = new List<object>();
        var errors = new List<object>();

        await using var db = new FinanceDbContext();

        // Build a hash set of existing rows once, so we can detect duplicates
        // in memory without N+1 queries.
        var existing = skipDuplicates
            ? (await db.Transactions
                .Select(t => new { t.Date, t.Amount, t.Merchant, t.Description })
                .ToListAsync(ct))
                .Select(t => HashKey(t.Date, t.Amount, t.Merchant, t.Description))
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        using var reader = new StreamReader(filePath);
        using var csv = new CsvReader(reader, config);

        await csv.ReadAsync();
        csv.ReadHeader();

        int rowNumber = 1; // header is row 1
        while (await csv.ReadAsync())
        {
            rowNumber++;
            try
            {
                var row = csv.GetRecord<StatementCsvRow>();
                var key = HashKey(row.Date, row.Amount, row.Merchant, row.Description);

                if (existing.Contains(key))
                {
                    skipped.Add(new { row = rowNumber, reason = "duplicate", row.Date, row.Amount, row.Merchant });
                    continue;
                }

                db.Transactions.Add(new Transaction
                {
                    Id = Guid.NewGuid(),
                    Date = row.Date,
                    Amount = row.Amount,
                    Merchant = row.Merchant,
                    Category = row.Category,
                    Description = row.Description
                });

                if (skipDuplicates)
                {
                    // Tracking keys as we go also catches a row repeated inside this
                    // one file, not just rows that were already in the database.
                    existing.Add(key);
                }

                imported.Add(new { row = rowNumber, row.Date, row.Amount, row.Merchant });
            }
            catch (Exception ex) when (ex is CsvHelperException or FormatException)
            {
                errors.Add(new { row = rowNumber, message = ex.Message });
            }
        }

        await db.SaveChangesAsync(ct);

        return new
        {
            file = filePath,
            importedCount = imported.Count,
            skippedCount = skipped.Count,
            errorCount = errors.Count,
            skipped = skipped.Take(5).ToList(),
            errors = errors.Take(5).ToList(),
            note = imported.Count > 0
                ? "Imported rows do not have embeddings yet. Restart the app so the embedding pass in Program.cs picks them up, or search will not find them."
                : null
        };
    }

    private static string HashKey(DateOnly date, decimal amount, string merchant, string description)
    {
        // "0.00" rather than a plain ToString(): the Amount column is numeric(18,2), so a
        // CSV value of -12.5 comes back from the database as -12.50. Hashing the raw
        // decimal would give the two different keys and the duplicate check would miss.
        var raw = $"{date:yyyy-MM-dd}|{amount.ToString("0.00", CultureInfo.InvariantCulture)}|{merchant}|{description}";
        var bytes = Encoding.UTF8.GetBytes(raw);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private sealed class StatementCsvRow
    {
        [Format("yyyy-MM-dd")]
        public DateOnly Date { get; set; }

        public decimal Amount { get; set; }

        public string Merchant { get; set; } = string.Empty;

        public string Category { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;
    }
}
```

> The `try`/`catch` is intentionally narrow. We catch `CsvHelperException` for malformed rows (bad date format, non-decimal amount, missing column) and `FormatException` for anything the converter throws past CsvHelper. Everything else (`IOException`, `DbUpdateException`, cancellation) bubbles up and aborts the import. That's the right behaviour. A locked file or a dead database isn't a per-row problem. It's a whole-operation problem, and the agent should see it as such.

> **CsvHelper's messages aren't shaped for a model.** A bad date doesn't produce "unparseable date". It produces `An unexpected error occurred.` followed by roughly 250 characters of `IReader`/`IParser` state. It works here only because that dump happens to include `RawRecord`, which is enough for the model to name the offending row. Five of them in one response is several hundred wasted tokens. Trimming to `ex.Message.Split('\n')[0]` plus the raw record is a worthwhile exercise, and it's the same lesson as the `note` field: you own the shape of what the agent reads.

> **About the embedding note.** New rows go in without an `Embedding` value. The startup block in `Program.cs` only embeds when the app boots, so freshly imported rows are invisible to `SearchTransactions` until the next restart. The `note` field in the return is how the agent finds out at all. Whether it passes that on to you, and whether the sentence it writes still contains an actionable "restart the app", is its own decision. A cleaner solution would be to inject `IEmbeddingGenerator` into this tool and embed inline. We leave that as an exercise. The teaching point is that tool return shape is how the agent learns what to say next.

---

## Step 2: Register the tool

Where the tool list lives depends on how far through the workshop you are. Open `src/FinanceAssistant/Program.cs` and find out which of the three you have.

### Coming straight from P2.02

Find the block that instantiates the existing tools and add a sibling line:

```csharp
var importStatement = new ImportStatementTool();
```

Then add it to the `ChatOptions.Tools` list. It ends up like this, with the fourth entry as the new one:

```csharp
var chatOptions = new ChatOptions
{
    Tools =
    [
        AIFunctionFactory.Create(convertCurrency.Convert),
        AIFunctionFactory.Create(getTransactions.GetTransactions),
        AIFunctionFactory.Create(searchTransactions.SearchTransactions),
        AIFunctionFactory.Create(importStatement.ImportStatement)
    ]
};
```

### If you did P2.B02 or P5.01

Your list is longer than the one above. It holds `analyzeExpenses`, or the gated transfer tool, or both.

Do not paste the block above over your list. You'll lose those tools.

Keep the entries you have. Add the new one alongside them. If the gated transfer tool is in the list, put the new registration above it:

```csharp
        AIFunctionFactory.Create(importStatement.ImportStatement),
        new ApprovalRequiredAIFunction(AIFunctionFactory.Create(transferFunds.Transfer))
```

> `transferFunds` only exists once P5.01 lands, so pasting that line at P2.02 gives you `CS0103: The name 'transferFunds' does not exist in the current context`. `ApprovalRequiredAIFunction` itself compiles fine either way. It's a framework type from `Microsoft.Extensions.AI`, which `Program.cs` already imports. Nothing enforces the gate until P5.01 adds the check in the agent loop.

### If you did P5.02 or later

`Program.cs` no longer builds the list. It reads `Tools = AgentToolset.CreateTools(embedder)` and there's nothing in that file to extend.

Open `src/FinanceAssistant/AgentToolset.cs` instead. Add both lines inside `CreateTools`:

```csharp
var importStatement = new ImportStatementTool();

return
[
    // the entries already here, unchanged
    AIFunctionFactory.Create(importStatement.ImportStatement)
];
```

> This is the better place to land. `tests/FinanceAssistant.Evals/AgentUnderTest.cs` builds its `ChatOptions` from the same `CreateTools` call, so one edit registers the tool with the agent and with the eval harness at the same time. That's what P5.02 bought when it moved the list out of `Program.cs`: a single definition of "the agent's tools", so an eval can never grade a set the agent no longer ships.

---

## Step 3: Run

Smoke test first. Make a tiny CSV at `/tmp/test-import.csv`:

```csv
Date,Amount,Merchant,Category,Description
2026-05-01,-12.50,Test Coffee,Restaurants,Bonus import smoke test
2026-05-02,-9.99,Test Sub,Subscriptions,Bonus import smoke test
```

Run the app:

```bash
dotnet run --project src/FinanceAssistant
```

In the REPL, type:

```
Import the statement at /tmp/test-import.csv
```

Expected behaviour:

1. The agent calls `ImportStatement`.
2. The tool returns `importedCount: 2, skippedCount: 0, errorCount: 0`. Those counts are deterministic. Check them first.
3. The agent replies with something like "Imported 2 transactions from /tmp/test-import.csv." It usually passes on some version of the restart warning from the `note` field, but not always the useful version: `SystemPrompt.md` tells it to be concise, and it will sometimes compress "restart the app" into "once the embedding pass runs", which no longer tells you to do anything. That's the model's editorial call, not a bug in your tool. What you control is that `note` is in the return value. Ask the agent to repeat the tool result verbatim if you want to see it.

Now try the same prompt again. The agent should call the tool again and this time get `importedCount: 0, skippedCount: 2`, because the SHA-256 hashes match existing rows.

Finally, break a row on purpose. Replace the date on the `Test Coffee` line with `not-a-date` and re-run the import. You should see `importedCount: 0`, `skippedCount: 1` (the `Test Sub` row is a duplicate by now) and `errorCount: 1`. The tool counts the header as row 1, so the broken line is reported as row 2, not row 1.

Those two smoke-test rows stay in the database and will show up in later exercises. Clear them when you're done:

```bash
docker compose exec postgres psql -U postgres -d financeassistant \
  -c "DELETE FROM \"Transactions\" WHERE \"Description\" = 'Bonus import smoke test';"
```

---

## Troubleshooting

### `file_not_found` even though the file exists

You passed a path starting with `~`. Nothing expands it. You typed it into the agent's REPL, not into a shell, so the tool receives the literal string `~/Downloads/statement.csv` and `File.Exists` returns false. Pass a fully resolved path like `/Users/me/Downloads/statement.csv`.

### Every row lands in `errors` with "Field with name 'Date' does not exist"

Your CSV headers don't match the canonical schema. Open the file. The first line has to name all five columns exactly: `Date`, `Amount`, `Merchant`, `Category`, `Description`. Excel and Numbers sometimes export with quoted headers (`"Date","Amount",...`), which CsvHelper handles fine, and column order doesn't matter because CsvHelper matches on name. Different names do matter: a bank export with `Transaction Date` or `Debit` won't auto-map. Rename the headers.

### Amounts with thousands separators import with the wrong value

Neither form throws, which is what makes this one dangerous.

Quoted, `"1,234.56"` parses correctly to `1234.56`. CsvHelper's decimal converter uses `NumberStyles.Number`, which allows grouping, and `InvariantCulture`'s group separator is `,`.

Unquoted, `1,234.56` is two CSV fields, not one. The row now has six fields where five are expected, so every column after `Amount` shifts by one. The row imports cleanly with `Amount = 1.00` and the merchant name sitting in the wrong column. `errorCount` stays at `0` and the agent tells you it worked.

Strip thousands separators before importing, and spot-check the first few rows in the database after a first import from a new source.

### Agent calls `ImportStatement` for questions like "show me last month's spending"

Your tool description is too permissive. The model is reading "import a bank statement" as roughly equivalent to "look at transactions". Sharpen the first sentence: "This tool MODIFIES the database. Only call it when the user explicitly asks to import, load, or upload a statement file." Restart the app so the description re-registers.

### The whole import fails with a database error and nothing is written

One of your fields is longer than its column allows: `Merchant` 200, `Category` 100, `Description` 2000. Length violations don't surface per row. They surface as a `DbUpdateException` from the single `SaveChangesAsync` after the loop, so they abort the entire import, good rows included. The agent usually misdiagnoses this and tells you the file is missing or the headers are wrong. They aren't.

This is a genuine gap in the partial-success story, and long memo lines in real bank exports are exactly how you hit it. Fixing it is a good exercise: validate the three lengths inside the loop and push over-long rows into `errors` alongside the parse failures, or truncate them before `db.Transactions.Add`.

### Imported rows do not show up in `SearchTransactions`

Expected. New rows have `Embedding = null`. Restart the app. The embedding pass in `Program.cs` picks them up on the next boot. If you want inline embedding, inject `IEmbeddingGenerator<string, Embedding<float>>` through the constructor and embed each row before adding it to the context.

---

## You can now

Grow the dataset from inside the agent loop:

- "Import the statement at /tmp/april.csv" hits `ImportStatement` and inserts the rows.
- "Now show me what I spent on restaurants in April" hits `GetTransactions` against the rows you just imported.

You also have a clean example of a tool that mutates state, with three properties worth copying into future tools: a description that admits the side effect, partial-success semantics that let one bad row pass through without poisoning the rest, and a content-hash duplicate check that makes the operation safe to retry.

---

## Summary

You've added:

- **`Tools/ImportStatementTool.cs`**: a write tool that ingests CSV statements row by row.
- **Defensive parsing**: per-row `try`/`catch`, capped error reporting, narrow exception catch.
- **Content-hash idempotency**: `SHA-256` of `(Date, Amount, Merchant, Description)` to detect re-imports.
- **A return shape that drives follow-up phrasing**: counts, samples of failures, and a `note` field that tells the agent about the embedding lag.

---

## What's next

P2.B02 is a natural follow-on. With an `ImportStatement` tool in hand you can drop fresh data into the database mid-conversation, then ask the agent to analyse it. The analysis tool lives at `p2-b02-analyze-expenses.md`.

If you want to extend `ImportStatement` itself, two good directions:

1. Inject `IEmbeddingGenerator` and embed rows inline so `SearchTransactions` sees them immediately.
2. Wrap the tool with `ApprovalRequiredAIFunction` from P5.01 so imports require confirmation. Writes are exactly the kind of operation that gate was built for.

---

## Additional Resources

- [CsvHelper documentation](https://joshclose.github.io/CsvHelper/)
- [Microsoft.Extensions.AI tool calling](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)
