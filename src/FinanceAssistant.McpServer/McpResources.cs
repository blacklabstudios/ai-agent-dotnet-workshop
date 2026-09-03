using System.ComponentModel;
using FinanceAssistant.Data;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace FinanceAssistant.McpServer;

[McpServerResourceType]
public class McpResources
{
    [McpServerResource(UriTemplate = "finance://categories", Name = "categories", MimeType = "text/plain")]
    [Description("The distinct list of categories present in the user's transactions, one per line. Use this to learn what category names are valid before filtering.")]
    public static async Task<string> Categories(CancellationToken ct = default)
    {
        await using var db = new FinanceDbContext();
        var categories = await db.Transactions
            .Select(t => t.Category)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync(ct);

        return string.Join('\n', categories);
    }

    [McpServerResource(UriTemplate = "finance://categories/{category}", Name = "category_transactions", MimeType = "text/plain")]
    [Description("The most recent transactions in a given category, one per line. Pair with finance://categories: list the categories first, then drill into one.")]
    public static async Task<string> CategoryTransactions(
        string category,
        CancellationToken ct = default)
    {
        await using var db = new FinanceDbContext();
        var transactions = await db.Transactions
            .Where(t => t.Category == category)
            .OrderByDescending(t => t.Date)
            .Select(t => new { t.Date, t.Amount, t.Merchant, t.Description })
            .Take(50)
            .ToListAsync(ct);

        if (transactions.Count == 0)
        {
            return $"No transactions found in category '{category}'.";
        }

        return string.Join('\n', transactions.Select(t =>
            $"{t.Date:yyyy-MM-dd}  {t.Amount,10:0.00}  {t.Merchant,-30}  {t.Description}"));
    }
}
