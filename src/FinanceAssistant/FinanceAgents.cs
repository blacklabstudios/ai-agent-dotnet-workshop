using FinanceAssistant.Tools;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace FinanceAssistant;

// One definition of the two agents and the handoffs between them, shared by the REPL in
// Program.cs and by the eval harness in tests/FinanceAssistant.Evals. It replaces
// AgentToolset, for the reason that class existed: an eval that builds its own agents
// grades a system you stopped shipping.
public static class FinanceAgents
{
    // Also the AuthorName on every message each agent writes, which is how the evals
    // tell the two apart.
    public const string AnalystName = "data_analyst";
    public const string CoachName = "money_coach";

    public static Workflow CreateWorkflow(
        IChatClient chatClient,
        IEmbeddingGenerator<string, Embedding<float>> embedder)
    {
        // Prompts/*.md is copied next to whichever assembly runs this, the REPL's or the
        // eval project's, so both read the files the agent ships.
        var analystPrompt = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Prompts", "AnalystPrompt.md"));
        var coachPrompt = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Prompts", "CoachPrompt.md"));

        var convertCurrency = new ConvertCurrencyTool();
        var getTransactions = new GetTransactionsTool();
        var searchTransactions = new SearchTransactionsTool(embedder);
        var sumTransactions = new SumTransactionsTool();

        var analyst = new ChatClientAgent(
            chatClient,
            instructions: analystPrompt,
            name: AnalystName,
            description: "Specialist in transaction data and currency conversion",
            tools:
            [
                AIFunctionFactory.Create(convertCurrency.Convert),
                AIFunctionFactory.Create(getTransactions.GetTransactions),
                AIFunctionFactory.Create(searchTransactions.SearchTransactions),
                AIFunctionFactory.Create(sumTransactions.SumTransactions)
            ]);

        var coach = new ChatClientAgent(
            chatClient,
            instructions: coachPrompt,
            name: CoachName,
            description: "Financial coach who interprets findings and recommends next steps",
            tools: []);

        return AgentWorkflowBuilder
            .CreateHandoffBuilderWith(coach)
            .WithHandoffs(coach, [analyst])
            .WithHandoffs(analyst, [coach])
            // The hand-back is the model's call, and it often skips it. When the Analyst
            // answers without one, ask it once more to hand back instead of ending the turn.
            .WithAutonomousMode(
                turnLimit: 1,
                continuationPrompt: "Hand the conversation back to the Money Coach now.",
                agents: [analyst])
            .Build();
    }
}
