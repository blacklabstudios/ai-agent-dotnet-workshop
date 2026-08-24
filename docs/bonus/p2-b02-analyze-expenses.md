# P2.B02 - Analyze Expenses with the 50/30/20 Rule

> Pillar 2 bonus. Optional. Pick up after P2.02.

## Mission

Add an `AnalyzeExpenses` tool that compares a date range with the 50/30/20 budget rule. Ask "How is my budget looking in April?" and get actual percentages, targets, and the difference across Needs, Wants, and Savings.

The agent can answer "Am I overspending?" with numbers.

**Learning Objectives**:

- Return aggregates instead of rows the model has to count
- Map transaction categories to the 50/30/20 framework
- Set useful defaults for an LLM caller
- Keep facts in the tool and explanation in the model
- Limit the untrusted text a tool can pass to the model

---

## Prerequisites

- P2.02 finished. The agent has `GetTransactions` and `SearchTransactions` working and the database has transactions in it.
- A date range you have data for. The seed file runs from 2024-06-01 to 2026-06-15. Pick a month inside that window for your first test. This guide uses April 2026.

> If you also did P2.B01, you can import a real statement first and then run the analysis against it. The flow is the point.

---

## What we're solving

`GetTransactions` returns a list of rows. `SearchTransactions` returns a list of rows. If the user asks "where is my money going", the model gets a list of rows and has to count, group, and compute percentages itself. That works sometimes. It also drifts. Decimal arithmetic in token space is unreliable. With 30 transactions the model might be fine. With 300 it will quietly round, miscount, or hallucinate a category that doesn't exist in the data.

Return aggregates instead of rows. The 50/30/20 framework gives the tool a clear shape:

- **Needs** (target 50%): rent, groceries, utilities, healthcare, transport, insurance. The things you can't easily skip.
- **Wants** (target 30%): restaurants, entertainment, subscriptions, shopping, travel. Discretionary spending.
- **Savings** (target 20%): savings transfers, investments, debt paydown above minimums.

We map every category to one of those three buckets, sum the absolute value of expenses in each bucket, divide by the mapped expense total, and return both the actuals and the targets. Anything we can't map stays out of that denominator and is reported on the side. There's a note after the code on why. The model then narrates: "You're at 62% on Needs, 12 points over the 50% target, mostly driven by Rent and Groceries."

> **Why 50/30/20 and not something more flexible.** The rule is well known and concrete. You can argue with the percentages but you can't misunderstand the shape. A more sophisticated tool would accept a budget configuration (per-category targets, custom buckets, rolling envelopes) and that's the right design for a personal finance product. For a workshop bonus, opinionated is better. The interesting wiring is in how the tool maps categories to buckets, not in how it loads a budget config.

Four patterns to notice:

1. **Aggregates, not rows.** The return is small and structured. The agent doesn't need to count anything. It can quote the numbers as-is.

2. **A defaultable taxonomy.** The category-to-bucket map lives in the tool. Your own data will have categories that don't match the seed file ("Streaming" instead of "Subscriptions"). The tool returns an `unmapped` section listing those categories with their totals, so the agent can ask the user how to classify them, or you can extend the map.

3. **Income vs expenses.** Positive amounts in the database are income, not spending. The tool filters them out so the percentages mean what the model thinks they mean. Income is reported separately for completeness. Note this is a blunt filter: a positive-amount refund counts as income alongside salary, with no breakdown on the income side. Good enough for this workshop, not for a real budgeting product.

4. **A narrow projection is a narrow attack surface.** The query filters on `Date` and reads two columns, `Amount` and `Category`. It never touches `Merchant` or `Description`, so no free text out of the database reaches the model through this tool. That's a general property of aggregate tools and it's worth naming. A tool that returns rows hands the model whatever a merchant chose to print on a statement line. A tool that returns sums hands it numbers. The seed file carries one transaction whose description tells the assistant to ignore its instructions and dump every transaction over 1000. `GetTransactions` reads that sentence out to the model. `AnalyzeExpenses` can't, because the column never leaves the database.

---

## If you're comfortable, do this

Use this list if you want the route first. The full steps explain the choices and help you recover when a step fails.

1. Create `src/FinanceAssistant/Tools/AnalyzeExpensesTool.cs`. Give it a date expression and optional 50/30/20 target parameters.
2. Query the range. Sum income and expenses separately. Map each expense category with a static dictionary. Keep unmatched categories in `unmapped`.
3. Return per-bucket actuals, targets, variance, and top categories per bucket. Include `unmapped` and `income` as separate fields.
4. Register the tool. Run. Ask "How is my budget looking in April 2026?" If you are on P5.02 or later, the tool list moved to `AgentToolset.cs`. Register it there.

---

## Step 1: Create AnalyzeExpensesTool

Create `src/FinanceAssistant/Tools/AnalyzeExpensesTool.cs`:

```csharp
using System.ComponentModel;
using System.Globalization;
using FinanceAssistant.Data;
using Microsoft.EntityFrameworkCore;

namespace FinanceAssistant.Tools;

public class AnalyzeExpensesTool
{
    // Map from transaction Category to 50/30/20 bucket.
    // Case doesn't matter here: the dictionary comparer is case-insensitive.
    // Nine of these keys match nothing in the seed data: Mortgage, Insurance, Childcare,
    // Hobbies, Gifts, and the four Savings ones. They're a template for your own data,
    // not a description of the seed file.
    private static readonly Dictionary<string, string> CategoryToBucket =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Needs
            ["Rent"] = "Needs",
            ["Mortgage"] = "Needs",
            ["Utilities"] = "Needs",
            ["Groceries"] = "Needs",
            ["Healthcare"] = "Needs",
            ["Transport"] = "Needs",
            ["Insurance"] = "Needs",
            ["Childcare"] = "Needs",

            // Wants
            ["Restaurants"] = "Wants",
            ["Entertainment"] = "Wants",
            ["Subscriptions"] = "Wants",
            ["Shopping"] = "Wants",
            ["Travel"] = "Wants",
            ["Hobbies"] = "Wants",
            ["Gifts"] = "Wants",

            // Savings
            ["Savings"] = "Savings",
            ["Investments"] = "Savings",
            ["Retirement"] = "Savings",
            ["DebtPaydown"] = "Savings"
        };

    [Description(
        "Analyse spending against the 50/30/20 budgeting rule (Needs 50%, Wants 30%, Savings 20%). " +
        "Returns total expenses, total income, the share each bucket consumed of total expenses, " +
        "variance from the target percentages, and the top categories inside each bucket. " +
        "Use this when the user asks about budget, spending breakdown, where their money is going, or how they are doing against a target.")]
    public async Task<object> AnalyzeExpenses(
        [Description("ISO 8601 date or range. Pass '2026-03-01..2026-03-31' for a full month or '2026-01-01..2026-12-31' for a year. A bare month like '2026-03' is NOT supported.")] string dateExpression,
        [Description("Target percentage for Needs. Default 50.")] double needsTarget = 50,
        [Description("Target percentage for Wants. Default 30.")] double wantsTarget = 30,
        [Description("Target percentage for Savings. Default 20.")] double savingsTarget = 20,
        CancellationToken ct = default)
    {
        DateOnly start, end;
        try
        {
            (start, end) = ParseRange(dateExpression);
        }
        catch (FormatException ex)
        {
            return new
            {
                error = "invalid_date",
                hint = "Use ISO 8601: YYYY-MM-DD for a single day, or YYYY-MM-DD..YYYY-MM-DD for a range.",
                message = ex.Message
            };
        }

        if (Math.Abs(needsTarget + wantsTarget + savingsTarget - 100) > 0.01)
        {
            return new
            {
                error = "invalid_targets",
                hint = "needsTarget + wantsTarget + savingsTarget must sum to 100.",
                provided = new { needsTarget, wantsTarget, savingsTarget }
            };
        }

        await using var db = new FinanceDbContext();

        var rows = await db.Transactions
            .Where(t => t.Date >= start && t.Date <= end)
            .Select(t => new { t.Amount, t.Category })
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return new
            {
                range = new { start, end },
                message = "No transactions in this range.",
                totalExpenses = 0m,
                totalIncome = 0m
            };
        }

        var income = rows.Where(r => r.Amount > 0).Sum(r => r.Amount);
        var expenses = rows.Where(r => r.Amount < 0).ToList();
        var totalExpenses = expenses.Sum(r => Math.Abs(r.Amount));

        if (totalExpenses == 0)
        {
            return new
            {
                range = new { start, end },
                message = "No expenses in this range. Income only.",
                totalExpenses = 0m,
                totalIncome = income
            };
        }

        // Group expenses by category, then bucket each group.
        var byCategory = expenses
            .GroupBy(r => r.Category)
            .Select(g => new
            {
                Category = g.Key,
                Total = g.Sum(r => Math.Abs(r.Amount)),
                Bucket = CategoryToBucket.TryGetValue(g.Key, out var b) ? b : null
            })
            .ToList();

        var unmapped = byCategory
            .Where(c => c.Bucket is null)
            .OrderByDescending(c => c.Total)
            .Select(c => new { category = c.Category, total = c.Total })
            .ToList();

        var mappedTotal = byCategory.Where(c => c.Bucket is not null).Sum(c => c.Total);

        decimal BucketTotal(string bucket) =>
            byCategory.Where(c => c.Bucket == bucket).Sum(c => c.Total);

        IEnumerable<object> TopCategoriesIn(string bucket) =>
            byCategory
                .Where(c => c.Bucket == bucket)
                .OrderByDescending(c => c.Total)
                .Take(5)
                .Select(c => new
                {
                    category = c.Category,
                    total = c.Total,
                    share = Math.Round(SafePercent(c.Total, BucketTotal(bucket)), 1)
                });

        object Summary(string bucket, double target)
        {
            var total = BucketTotal(bucket);
            var actual = SafePercent(total, mappedTotal);
            return new
            {
                total,
                actualPercent = Math.Round(actual, 1),
                targetPercent = target,
                variancePoints = Math.Round(actual - target, 1),
                topCategories = TopCategoriesIn(bucket).ToList()
            };
        }

        return new
        {
            range = new { start, end },
            totalIncome = income,
            totalExpenses,
            mappedExpenses = mappedTotal,
            unmappedExpenses = unmapped.Sum(u => u.total),
            needs = Summary("Needs", needsTarget),
            wants = Summary("Wants", wantsTarget),
            savings = Summary("Savings", savingsTarget),
            unmapped,
            note = unmapped.Count > 0
                ? "Some categories are not mapped to a 50/30/20 bucket. They are excluded from the percentage math. Map them in CategoryToBucket if you want them counted."
                : null
        };
    }

    private static double SafePercent(decimal numerator, decimal denominator) =>
        denominator == 0 ? 0 : (double)(numerator / denominator) * 100.0;

    private static (DateOnly Start, DateOnly End) ParseRange(string expr)
    {
        const string format = "yyyy-MM-dd";

        if (expr.Contains("..", StringComparison.Ordinal))
        {
            var parts = expr.Split("..", 2, StringSplitOptions.None);
            if (parts.Length == 2
                && DateOnly.TryParseExact(parts[0], format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
                && DateOnly.TryParseExact(parts[1], format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
            {
                return (start, end);
            }
        }
        else if (DateOnly.TryParseExact(expr, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var single))
        {
            return (single, single);
        }

        throw new FormatException($"Cannot parse date expression: '{expr}'. Expected ISO 8601 date or range.");
    }
}
```

> **`ParseRange` is a copy, and this tool skips `TransactionsService`.** Both are on purpose. You wrote that parser in P2.02, and it's the same method down to the exception message, but the service keeps it private and the service returns rows. This tool needs sums, so it queries the context directly and projects only the two columns it adds up. Two small duplicated methods cost less than a shared helper that drags a row-shaped service into an aggregate-shaped tool. When a third tool wants the same range syntax, that's the moment to extract it.

> `BucketTotal` gets recomputed more than once per bucket: once directly in `Summary`, and again for each of that bucket's top categories inside `TopCategoriesIn`. `mappedTotal` is already computed once up front and reused via closure. For three buckets and a handful of categories this is fine and the code reads well. If you ever extend this to a 20-bucket envelope budget, precompute each bucket's total once and reuse it everywhere.

> **Why "percent of mapped" and not "percent of total".** The denominator for the 50/30/20 split is mapped expenses only, not all expenses. Unmapped categories sit on the side, visible to the agent, but they don't pollute the bucket percentages. If you have 30% of your spend in an unmapped category, the Needs/Wants/Savings shares would otherwise all look artificially low. The `note` field tells the agent this is happening.

---

## Step 2: Register the tool

Where the tool list lives depends on how far through the workshop you are. Open `src/FinanceAssistant/Program.cs` and find out which of the three you have.

### Coming straight from P2.02

Add the instantiation alongside the other tools:

```csharp
var analyzeExpenses = new AnalyzeExpensesTool();
```

Then add it to `ChatOptions.Tools`. This is the plain P2.02 shape, so it compiles standalone:

```csharp
var chatOptions = new ChatOptions
{
    Tools =
    [
        AIFunctionFactory.Create(convertCurrency.Convert),
        AIFunctionFactory.Create(getTransactions.GetTransactions),
        AIFunctionFactory.Create(searchTransactions.SearchTransactions),
        AIFunctionFactory.Create(analyzeExpenses.AnalyzeExpenses)
    ]
};
```

### If you did P2.B01 or P5.01

Your list is longer than the one above. It holds `importStatement`, or the gated `new ApprovalRequiredAIFunction(AIFunctionFactory.Create(transferFunds.Transfer))`, or both.

Do not paste the block above over your list. You'll lose those tools.

Keep the entries you have. Add the `analyzeExpenses` line alongside them.

### If you did P5.02 or later

`Program.cs` no longer builds the list. It reads `Tools = AgentToolset.CreateTools(embedder)` and there's nothing in that file to extend.

Open `src/FinanceAssistant/AgentToolset.cs` instead. Add both lines inside `CreateTools`:

```csharp
var analyzeExpenses = new AnalyzeExpensesTool();

return
[
    // the entries already here, unchanged
    AIFunctionFactory.Create(analyzeExpenses.AnalyzeExpenses)
];
```

> This is the better place to land, and it's worth seeing why. `tests/FinanceAssistant.Evals/AgentUnderTest.cs` builds its `ChatOptions` from the same `CreateTools` call. One edit registers the tool with the agent and with the eval harness at the same time. That's exactly what P5.02 bought when it moved the list out of `Program.cs`: a single definition of "the agent's tools", so an eval can never grade a set the agent no longer ships.

---

## Step 3: Run

From the repo root:

```bash
dotnet run --project src/FinanceAssistant
```

Try three prompts in turn:

- `How is my budget looking in April 2026?`
  Agent should call `AnalyzeExpenses` with `2026-04-01..2026-04-30`. April 2026 is the only month in the seed file whose `unmapped` list comes back with two entries instead of one, roughly 405 of the month's 2968 in expenses. Watch two things. What the agent does with the variance numbers, and whether it passes on the `note` about unmapped categories rather than quietly quoting percentages that don't cover all the spend.

- `Am I overspending on wants this year?`
  Agent should call with a full-year range and quote the Wants bucket. Bonus points if it explains the variance. "This year" resolves relative to today, so the exact range it picks (and the numbers) depends on when you run this. The model may not restate the range unprompted, since the system prompt asks it to be concise. If you want to confirm what it used, ask in the same message rather than as a separate follow-up turn, e.g. "Am I overspending on wants this year? Also tell me the exact date range you used." Coming straight from P2.02, the app has no conversation memory yet (that lands in P4.01), so a follow-up question in the next turn has nothing to look back at. From P4.01 on it does, and either phrasing works.

- `Compare August and September 2025`
  Multi-tool reasoning. The agent will typically call `AnalyzeExpenses` twice (once per month) and narrate the diff, though it may also reach for `GetTransactions` alongside it. September 2025 is the thinnest month in the seed file, six rows and five expenses, with rent at 94% of the spending, so the contrast is stark. Per-bucket totals earn their keep over per-row tools here. They don't make the model infallible, though. It can still misstate a figure in free-form prose while getting the headline percentages right, so if the narrative reads oddly, compare it against what the tool actually returned. The next section shows how to see that.

### Seeing the raw tool output

Nothing in this workshop ever prints what a tool returned. Coming straight from P2.02, the REPL prints the agent's reply and nothing else. P3.01 adds a line naming the tools each iteration calls, and that's the name only, never the result. So on every branch, the recipe below is how you read the JSON. Split the final `return` in `AnalyzeExpenses` in two and print the object on its way past:

```csharp
var result = new
{
    range = new { start, end },
    // the rest of the return, unchanged
};

Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
    result,
    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

return result;
```

This covers the success path. The early returns for an empty range or a bad date are short enough to recognise from the reply alone.

Undo all three edits when you're done, the two added statements and the changed `return new` line. Leave `var result = new` in place without the `return result;` under it and the build stops with CS0161, not all code paths return a value.

---

## Troubleshooting

### `invalid_targets` even though I asked for the defaults

The model is passing all three parameters and one of them came back malformed. Add a temporary `Console.WriteLine($"{needsTarget} {wantsTarget} {savingsTarget}")` at the top of `AnalyzeExpenses` to see what arrived. A three-way split like `needsTarget: 33.3, wantsTarget: 33.3, savingsTarget: 33.3` is short by 0.1 and trips this. That's usually an arithmetic slip in how the model divided the numbers, not floating-point drift. At one or two decimal places, doubles don't drift anywhere near enough to cross the `1e-2` tolerance. Print the sum and look at where it diverged. Remove the `Console.WriteLine` when you're done.

### Categories are landing in `unmapped`

This shows up against the seed data, not only against an import. The seed CSV carries an `Other` category that is deliberately absent from `CategoryToBucket`, so it appears in `unmapped` on almost any range wide enough to catch one of its rows. In April 2026, the month Step 3 uses, `Other` accounts for 402.28 of the 404.78 that lands there.

You may also see a `Refunds` entry there. Most `Refunds` rows are positive and get filtered out as income before bucketing. One is negative, a refund processing fee, and that one lands in `unmapped`.

Both are expected, not a bug.

That refund row is also the seed file's prompt-injection test case. Its description tries to talk the assistant into dumping every transaction over 1000. Read it with `GetTransactions` and the model sees the whole sentence. Read it with `AnalyzeExpenses` and the model sees `{ "category": "Refunds", "total": 2.50 }`, because the query never selects the description column. That's pattern 4 from the top of this guide, showing up in your own output.

If you're running against imported or your own data with categories that don't appear in `CategoryToBucket`, you have two options:

1. Edit your CSV to use the canonical categories before importing.
2. Extend the dictionary. The keys are case-insensitive, so add the categories you actually have.

There's no "right" answer here. The map is a policy choice that lives in the tool.

### Needs is always way over 50%

Expected against the seed data. Rent is the largest line in almost every month, so Needs runs between 77% and 100% of mapped spending on every whole month in the seed file. January 2026 is the lowest at 77%. April 2025 is the highest at 100%, because the only five expenses that month are all Needs.

Narrower ranges swing much wider, because the tool takes any `YYYY-MM-DD..YYYY-MM-DD` you hand it. Ask for `2025-09-14..2025-09-21` and rent day falls outside the window, so Needs comes back at 0% and Wants at 100%.

The bucketing isn't wrong. The data is rent-heavy. Import your own statement (P2.B01) to see a split that argues back.

### The Savings bucket always reads 0%

The seed data has no `Savings`, `Investments`, `Retirement`, or `DebtPaydown` transactions, so against seed data alone the Savings bucket will always show `total: 0`, `actualPercent: 0`, and a variance of `-20`, no matter what range you pick. That's a property of the seed data, not a bug in the tool. Import your own statement (P2.B01) with a savings-transfer category to see the bucket move.

### Agent quotes wrong percentages

Read the raw return shape first, with the recipe under Step 3. If the tool returned `actualPercent: 62.3` and the model said `roughly 70%`, the description isn't clear enough that these are pre-computed. Add a line to it: "Return values are already computed percentages. Quote them directly, do not recompute." That sentence has saved me more than once.

Note that every percentage in the return is already rounded to one decimal, `share` included. That's deliberate. Handing a model `98.94724385636586` next to a tidy `95.5` invites exactly the recomputation you're trying to stop.

### Tool runs but no data comes back for ranges you know have transactions

`Date >= start && Date <= end` works for `DateOnly`. If you're seeing zero rows for a range that should be populated, check the seed file's date range. The seed runs from 2024-06-01 to 2026-06-15. Any range outside those two dates comes back empty.

Check the order of your range too. `2025-09-30..2025-09-01` parses cleanly and returns nothing, because no date is both after the 30th and before the 1st.

### The Wants bucket includes spending I consider essential

Categorisation is subjective, and the shipped map is one opinion, not a law. `Transport` is already mapped to Needs out of the box, so this usually shows up with your own imported data: a ride-hailing or transit line item comes in tagged `Travel`, which the map buckets as Wants, even though you consider that commute a Need. Two fixes:

1. Retag those rows to `Transport` before importing.
2. Add a distinct key for that category (e.g. `Commute`) and map it to `Needs` yourself.

The dictionary is the policy. There's no universally right split, only the one that matches how you think about your own money.

---

## You can now

Ask budget-shape questions and watch the agent answer with real numbers:

- "How is my budget looking in April 2026?" pulls a one-month aggregate, with unmapped spending in it.
- "Am I overspending on wants this year?" pulls a one-year aggregate and quotes the Wants variance.
- "Compare August and September 2025" triggers two calls and a narrative diff.

You also have a working example of the tool/model split: the tool returns the table, the model writes the story. Once that shape is in your toolbox you'll start to see it everywhere. Most "analytics" tools should be aggregates with light structure, not raw queries.

---

## Summary

You've added:

- **`Tools/AnalyzeExpensesTool.cs`**: a tool that returns 50/30/20 aggregates over a date range.
- **A category-to-bucket map** that lives in the tool and is the single place to evolve the taxonomy.
- **Defaultable targets** so the agent can call the tool without thinking about parameters in the common case, and can override them when the user has a custom budget.
- **An `unmapped` escape hatch** so categories outside the map remain visible without polluting the percentages.
- **A two-column projection** that keeps merchant names and descriptions out of the model's context, and the injected instructions hiding in one of them with it.

---

## What's next

This tool is the smallest interesting analytics tool. Three natural extensions:

1. **Month-over-month deltas.** Add a `compareToPrevious: true` parameter that returns last-period figures alongside this one. The agent can then say "Wants are up 8 points versus August" without two tool calls.

2. **Per-merchant drill-down.** Add a sibling tool `MerchantBreakdown(category, dateExpression)` that returns top merchants inside a category. Useful when the user asks "what's driving my Restaurants spend?". Note what this costs you: merchant names are free text the bank didn't write, so this tool hands the model exactly the untrusted strings pattern 4 kept out. That's not a reason to skip it. It's a reason to know which of your tools are on that side of the line, and to test this one against the seed file's injected row.

3. **Forecast.** Take the last N months of data, compute the average per bucket, and project the remainder of the current month or year. Be careful with the description so the model doesn't present projections as facts.

Each of these is one or two hours of work and reinforces the same lesson: small, opinionated, structured returns beat raw rows for narrative tasks.

---

## Additional Resources

- [Investopedia on the 50/30/20 rule](https://www.investopedia.com/ask/answers/022916/what-502030-budget-rule.asp)
- [Microsoft.Extensions.AI tool calling](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)
