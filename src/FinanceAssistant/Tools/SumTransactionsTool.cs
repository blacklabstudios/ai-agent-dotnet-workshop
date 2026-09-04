using System.ComponentModel;
using FinanceAssistant.Data;
using FinanceAssistant.Services;

namespace FinanceAssistant.Tools;

public class SumTransactionsTool
{
    [Description("Add up the transactions in a given date range, optionally for one category, and return the exact total, how many transactions it covers, and the average per transaction. Use this for every 'how much' question instead of adding amounts yourself. Expenses are negative. The dateExpression must be ISO 8601: a single date '2026-05-06' or a range '2026-01-01..2026-01-31'.")]
    public async Task<object> SumTransactions(
        [Description("ISO 8601 date or range, e.g. '2026-01-15' for one day or '2026-01-01..2026-01-31' for a range.")] string dateExpression,
        [Description("Optional category to add up, e.g. 'Restaurants'. Matched case-insensitively. Leave it out to add up every category.")] string? category = null,
        CancellationToken ct = default)
    {
        try
        {
            await using var db = new FinanceDbContext();
            var service = new TransactionsService(db);
            var transactions = await service.GetTransactions(dateExpression, ct);

            // Same rows GetTransactions returns, so the total can't disagree with the list.
            var matching = string.IsNullOrWhiteSpace(category)
                ? transactions
                : transactions.Where(t => string.Equals(t.Category, category, StringComparison.OrdinalIgnoreCase)).ToList();

            var total = matching.Sum(t => t.Amount);

            // The average is here because the Coach will reach for one. A figure it can quote
            // beats a figure it works out.
            return new
            {
                dateExpression,
                category,
                count = matching.Count,
                total,
                average = matching.Count == 0 ? 0 : Math.Round(total / matching.Count, 2)
            };
        }
        catch (FormatException ex)
        {
            return new
            {
                error = "invalid_date",
                hint = "Use ISO 8601: YYYY-MM-DD for a single date, or YYYY-MM-DD..YYYY-MM-DD for a range.",
                message = ex.Message
            };
        }
    }
}
