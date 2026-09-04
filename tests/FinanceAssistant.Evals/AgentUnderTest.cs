using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace FinanceAssistant.Evals;

// One turn of the real workflow, captured for grading.
internal sealed record AgentTurn(
    IReadOnlyList<ChatMessage> Messages,
    ChatResponse Response)
{
    public IReadOnlyList<FunctionCallContent> ToolCalls { get; } =
        Response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();

    public string ToolCallSummary =>
        ToolCalls.Count == 0 ? "no tool call" : string.Join(", ", ToolCalls.Select(c => c.Name));

    // One agent's share of the turn: its text, its tool calls and their results. MAF
    // stamps each message with the name of the agent that wrote it.
    public ChatResponse From(string agentName) =>
        new(Response.Messages.Where(m => m.AuthorName == agentName).ToList());
}

// The real Coach-and-Analyst workflow, run for one whole turn. MAF owns the loop now, so
// there is no ProposeNextStepAsync to stop at. We grade what the user would read, after
// every handoff and every tool call. That costs two things the Pillar 5 harness never
// paid: Postgres has to be up and embedded (run the REPL once first), and a turn is
// several model calls, not one.
internal static class AgentUnderTest
{
    private static readonly Lazy<Harness> Instance = new(Build);

    public static async Task<AgentTurn> RespondToAsync(
        string userMessage, CancellationToken ct = default)
    {
        var harness = Instance.Value;

        // A fresh workflow per call, so one eval's conversation never leaks into
        // another's and a green run cannot depend on the order the runner picked.
        var workflow = FinanceAgents.CreateWorkflow(harness.Client, harness.Embedder);

        List<ChatMessage> messages = [new(ChatRole.User, userMessage)];

        // Hosted through the workflow's own agent surface rather than the event loop
        // Program.cs drives by hand for live printing. Same agents, same handoffs, and one
        // call that returns only what this turn added: both agents' text, the handoffs,
        // and the tool calls.
        var result = await workflow.AsAIAgent().RunAsync(messages, cancellationToken: ct);

        var response = new ChatResponse(result.Messages.ToList());

        return new AgentTurn(messages, response);
    }

    private static Harness Build()
    {
        var config = EvalConfiguration.Load();

        var services = new ServiceCollection();
        services.AddChatClient(config);
        services.AddEmbeddingGenerator(config);

        // Not disposed on purpose: the client lives as long as the test host does.
        var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<IChatClient>();
        var embedder =
            provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        return new Harness(client, embedder);
    }

    private sealed record Harness(
        IChatClient Client,
        IEmbeddingGenerator<string, Embedding<float>> Embedder);
}
