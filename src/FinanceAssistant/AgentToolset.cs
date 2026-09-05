using FinanceAssistant.Tools;
using Microsoft.Extensions.AI;

namespace FinanceAssistant;

// One definition of "the agent's tools", shared by the REPL in Program.cs and by the
// eval harness in tests/FinanceAssistant.Evals. An eval that builds its own tool list
// grades a set the agent may no longer ship. Build it once, hand it to both.
public static class AgentToolset
{
    public static IList<AITool> CreateTools(IEmbeddingGenerator<string, Embedding<float>> embedder)
    {
        var convertCurrency = new ConvertCurrencyTool();
        var getTransactions = new GetTransactionsTool();
        var searchTransactions = new SearchTransactionsTool(embedder);
        var transferFunds = new TransferFundsTool();
        var importStatement = new ImportStatementTool();
        var analyzeExpenses = new AnalyzeExpensesTool();

        return
        [
            AIFunctionFactory.Create(convertCurrency.Convert),
            AIFunctionFactory.Create(getTransactions.GetTransactions),
            AIFunctionFactory.Create(searchTransactions.SearchTransactions),
            AIFunctionFactory.Create(importStatement.ImportStatement),
            AIFunctionFactory.Create(analyzeExpenses.AnalyzeExpenses),
            new ApprovalRequiredAIFunction(AIFunctionFactory.Create(transferFunds.Transfer))
        ];
    }
}
