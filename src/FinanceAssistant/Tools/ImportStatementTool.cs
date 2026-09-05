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
