using FinanceAssistant;
using FinanceAssistant.Data;
using FinanceAssistant.Tools;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pgvector;


await using (var db = new FinanceDbContext())
{
    await db.Database.EnsureCreatedAsync();
}

var config = new ConfigurationBuilder()
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();

var services = new ServiceCollection();
services.AddChatClient(config);
services.AddEmbeddingGenerator(config);
var provider = services.BuildServiceProvider();

var chatClient = provider.GetRequiredService<IChatClient>();

var embedder = provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

await using (var db = new FinanceDbContext())
{
    var unembedded = await db.Transactions
        .Where(t => t.Embedding == null)
        .ToListAsync();

    if (unembedded.Count > 0)
    {
        Console.WriteLine($"Embedding {unembedded.Count} transactions...");
        var texts = unembedded.Select(t => $"{t.Merchant} {t.Description}").ToList();
        var embeddings = await embedder.GenerateAsync(texts);
        for (int i = 0; i < unembedded.Count; i++)
        {
            unembedded[i].Embedding = new Vector(embeddings[i].Vector.ToArray());
        }
        await db.SaveChangesAsync();
        Console.WriteLine($"Embedded {unembedded.Count} transactions.");
    }
}

var analystPrompt = await File.ReadAllTextAsync(
    Path.Combine(AppContext.BaseDirectory, "Prompts", "AnalystPrompt.md"));
var coachPrompt = await File.ReadAllTextAsync(
    Path.Combine(AppContext.BaseDirectory, "Prompts", "CoachPrompt.md"));

var convertCurrency = new ConvertCurrencyTool();
var getTransactions = new GetTransactionsTool();
var searchTransactions = new SearchTransactionsTool(embedder);

var analyst = new ChatClientAgent(
    chatClient,
    instructions: analystPrompt,
    name: "data_analyst",
    description: "Specialist in transaction data and currency conversion",
    tools:
    [
        AIFunctionFactory.Create(convertCurrency.Convert),
        AIFunctionFactory.Create(getTransactions.GetTransactions),
        AIFunctionFactory.Create(searchTransactions.SearchTransactions)
    ]);

var coach = new ChatClientAgent(
    chatClient,
    instructions: coachPrompt,
    name: "money_coach",
    description: "Financial coach who interprets findings and recommends next steps",
    tools: []);

var workflow = AgentWorkflowBuilder
    .CreateHandoffBuilderWith(coach)
    .WithHandoffs(coach, [analyst])
    .WithHandoffs(analyst, [coach])
    .Build();

Console.WriteLine("Finance assistant (multi-agent). Type a message, or 'exit' to quit.");

List<ChatMessage> messages = new();

while (true)
{
    Console.Write("> ");
    var input = Console.ReadLine();
    if (input is null || string.Equals(input.Trim(), "exit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    messages.Add(new ChatMessage(ChatRole.User, input));

    await using StreamingRun run = await InProcessExecution.RunStreamingAsync(workflow, messages);
    await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

    string? lastExecutorId = null;
    List<ChatMessage> newMessages = new();

    await foreach (WorkflowEvent evt in run.WatchStreamAsync())
    {
        if (evt is AgentResponseUpdateEvent e)
        {
            if (e.ExecutorId != lastExecutorId)
            {
                lastExecutorId = e.ExecutorId;
                // ExecutorId looks like "money_coach_d08e4110e9c848eaa0823762ca570c17":
                // the agent name plus an underscore plus a 32-char instance id.
                // Strip the trailing _hex32 for display so the labels stay readable.
                var suffix = e.ExecutorId.LastIndexOf('_');
                var label = suffix > 0 ? e.ExecutorId[..suffix] : e.ExecutorId;
                Console.WriteLine();
                Console.WriteLine($"[{label}]");
            }

            Console.Write(e.Update.Text);
        }
        else if (evt is WorkflowOutputEvent outputEvt)
        {
            newMessages = outputEvt.As<List<ChatMessage>>()!;
            break;
        }
    }

    Console.WriteLine();

    // newMessages is the FULL workflow conversation after this turn, not the delta.
    // Skip the messages we already know about and append only what the workflow added.
    messages.AddRange(newMessages.Skip(messages.Count));
}

return 0;
