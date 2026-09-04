using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Xunit;

namespace FinanceAssistant.Evals;

// The Coach is allowed to coach. What it isn't allowed to do is invent the numbers it
// coaches on. Every amount it states has to come out of the Analyst's half of the turn:
// the Findings it wrote, or the rows its tools returned. No judge, so no tokens spent on
// grading and no second model to drift.
public partial class CoachGroundingEvals(ITestOutputHelper output)
{
    [Fact]
    public async Task Coach_only_quotes_amounts_the_analyst_produced()
    {
        var turn = await AgentUnderTest.RespondToAsync(
            "How much did I spend eating out in January 2026?",
            TestContext.Current.CancellationToken);

        var coach = turn.From(FinanceAgents.CoachName).Text;
        var analyst = turn.From(FinanceAgents.AnalystName);

        output.WriteLine($"[grounding] called: {turn.ToolCallSummary}");
        output.WriteLine($"[grounding] coach said: {coach}");

        var grounded = NumbersIn(analyst.Text)
            .Concat(analyst.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionResultContent>()
                .SelectMany(r => NumbersIn(JsonSerializer.Serialize(r.Result))))
            .ToHashSet();

        var quoted = AmountsIn(coach).ToList();
        output.WriteLine($"[grounding] coach quoted: {Format(quoted)}");

        // CoachPrompt.md tells the Coach to restate the key figure. A turn where it quotes
        // nothing passes the check below without testing anything, so it fails here.
        Assert.True(
            quoted.Count > 0,
            $"The Coach quoted no amount, so there is nothing to check. It said: {coach}");

        var invented = quoted.Where(a => !grounded.Contains(a)).ToList();

        Assert.True(
            invented.Count == 0,
            $"The Coach stated amounts the Analyst never produced: {Format(invented)}");
    }

    // An amount is a number with a currency symbol in front, or with exactly two decimals.
    // Counts, dates and percentages aren't amounts, and the Coach may say those freely.
    private static IEnumerable<decimal> AmountsIn(string text) =>
        AmountPattern().Matches(text).Select(m => Parse(m.Groups["n"].Value));

    private static IEnumerable<decimal> NumbersIn(string text) =>
        NumberPattern().Matches(text).Select(m => Parse(m.Value));

    // Thousands separators go, and decimal compares by value, so 2,389.85 and 2389.850
    // are the same amount.
    private static decimal Parse(string number) =>
        decimal.Parse(number.Replace(",", ""), CultureInfo.InvariantCulture);

    private static string Format(IEnumerable<decimal> amounts) =>
        string.Join(", ", amounts.Select(a => a.ToString(CultureInfo.InvariantCulture)));

    [GeneratedRegex(@"[€$£]\s?(?<n>\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d+(?:\.\d+)?)|(?<n>\b\d[\d,]*\.\d{2})\b")]
    private static partial Regex AmountPattern();

    [GeneratedRegex(@"\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d+(?:\.\d+)?")]
    private static partial Regex NumberPattern();
}
