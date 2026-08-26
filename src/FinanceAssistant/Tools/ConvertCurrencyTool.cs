using System.ComponentModel;

namespace FinanceAssistant.Tools;

public class ConvertCurrencyTool
{
    // Rates expressed in USD per unit of the source currency.
    // Hardcoded for the workshop. In a real system this would come from a live rates API,
    // typically injected as an IRatesService in the constructor.
    private static readonly Dictionary<string, decimal> RatesToUsd = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = 1.00m,
        ["EUR"] = 1.10m,
        ["GBP"] = 1.27m,
        ["JPY"] = 0.0067m,
        ["CHF"] = 1.13m,
        ["CAD"] = 0.74m,
        ["AUD"] = 0.66m,
    };

    [Description("Convert an amount from one currency to another using fixed reference rates. Returns a string like '100 EUR = 110.00 USD'. Supports USD, EUR, GBP, JPY, CHF, CAD, AUD.")]
    public string Convert(
        [Description("The amount to convert, in the source currency. A positive number.")] decimal amount,
        [Description("The 3-letter ISO currency code of the source amount, e.g. EUR, USD, GBP.")] string fromCurrency,
        [Description("The 3-letter ISO currency code of the target currency, e.g. USD, EUR, JPY.")] string toCurrency)
    {
        if (!RatesToUsd.TryGetValue(fromCurrency, out var fromRate))
            return $"Unknown source currency '{fromCurrency}'. Supported: {string.Join(", ", RatesToUsd.Keys)}.";

        if (!RatesToUsd.TryGetValue(toCurrency, out var toRate))
            return $"Unknown target currency '{toCurrency}'. Supported: {string.Join(", ", RatesToUsd.Keys)}.";

        var amountInUsd = amount * fromRate;
        var converted = amountInUsd / toRate;
        return $"{amount} {fromCurrency} = {converted:F2} {toCurrency}";
    }
}
