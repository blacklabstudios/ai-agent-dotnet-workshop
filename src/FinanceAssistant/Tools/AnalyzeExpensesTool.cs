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
