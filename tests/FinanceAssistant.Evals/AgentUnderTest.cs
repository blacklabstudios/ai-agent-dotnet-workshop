using FinanceAssistant.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FinanceAssistant.Evals;

// One turn of the real agent, captured for grading.
internal sealed record AgentTurn(
    IReadOnlyList<ChatMessage> Messages,
    ChatResponse Response,
    IList<AITool> Tools)
{
    public IReadOnlyList<FunctionCallContent> ToolCalls { get; } =
        Response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();

    public string ToolCallSummary =>
        ToolCalls.Count == 0 ? "no tool call" : string.Join(", ", ToolCalls.Select(c => c.Name));
}

// A real ChatAgent, asked for one turn and stopped at the ProposeNextStepAsync seam. The
// behaviour we grade is already decided there and no tool is ever invoked, so no Postgres,
// no embeddings call, no money moved.
internal static class AgentUnderTest
{
    private static readonly Lazy<Harness> Instance = new(Build);

    // The agent's real tool set, with no model call. For an eval that grades a recorded
    // response and still wants the judge to see every tool the agent ships.
    public static IList<AITool> Tools => Instance.Value.Options.Tools!;

    public static async Task<AgentTurn> RespondToAsync(
        string userMessage, CancellationToken ct = default)
    {
        Harness harness = Instance.Value;

        // A fresh store and agent per call, so one eval's conversation never leaks into
        // another's and a green run cannot depend on the order the runner picked.
        ConversationStore store = new();

        ChatAgent agent = new(
            harness.Client,
            harness.Options,
            store,
            harness.SystemPrompt,
            new SummarizingHistoryReducer(harness.Client));

        ChatResponse response = await agent.ProposeNextStepAsync(userMessage, ct);

        // store.Messages is the conversation the agent actually built and sent, not a
        // reconstruction of it.
        return new AgentTurn(store.Messages.ToList(), response, harness.Options.Tools!);
    }

    private static Harness Build()
    {
        IConfiguration config = EvalConfiguration.Load();

        ServiceCollection services = new();
        services.AddChatClient(config);
        services.AddEmbeddingGenerator(config);

        // Not disposed on purpose: the client lives as long as the test host does.
        ServiceProvider provider = services.BuildServiceProvider();

        IChatClient client = provider.GetRequiredService<IChatClient>();
        IEmbeddingGenerator<string, Embedding<float>> embedder =
            provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        ChatOptions options = new() { Tools = AgentToolset.CreateTools(embedder) };

        // The agent project's Prompts/SystemPrompt.md is copied into this project's
        // output by the project reference, so the eval reads the file the agent ships.
        string systemPrompt = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Prompts", "SystemPrompt.md"));

        return new Harness(client, options, systemPrompt);
    }

    private sealed record Harness(IChatClient Client, ChatOptions Options, string SystemPrompt);
}
