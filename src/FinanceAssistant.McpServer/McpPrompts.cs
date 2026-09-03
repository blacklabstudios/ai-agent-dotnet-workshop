using System.ComponentModel;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace FinanceAssistant.McpServer;

[McpServerPromptType]
public class McpPrompts
{
    [McpServerPrompt(Name = "monthly_spending_report")]
    [Description("Kick off a guided review of the user's spending for a given month. The client offers this as a one-click prompt and the user fills in the month.")]
    public static ChatMessage MonthlySpendingReport(
        [Description("Month in YYYY-MM format, e.g. '2026-01'.")] string month)
    {
        return new ChatMessage(
            ChatRole.User,
            $"Walk me through my spending in {month}. List the top three categories by total spend, " +
            $"flag any unusually large purchases, and call out any recurring subscriptions you find.");
    }
}
