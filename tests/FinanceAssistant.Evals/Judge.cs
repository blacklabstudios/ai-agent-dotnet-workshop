using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;

namespace FinanceAssistant.Evals;

// Mirrors the agent's construction in ServiceCollectionExtensions.cs, with two
// deliberate differences: a stronger deployment, and a reasoning budget. Same
// secrets, same pipeline, same kind of object, pointed somewhere else.
internal static class Judge
{
    // Built once, shared by every eval. The judge holds no per-test state, so there
    // is nothing to isolate, and a suite of twenty evals still builds one client.
    private static readonly Lazy<IChatClient> Instance = new(CreateClient);

    public static IChatClient Client => Instance.Value;

    private static IChatClient CreateClient()
    {
        IConfiguration config = EvalConfiguration.Load();

        // Every key in this method goes through Required, including the deployment
        // below. A null-forgiving `!` on any of them would trade this message for an
        // ArgumentNullException three frames away, inside ApiKeyCredential, naming
        // nothing you could act on.
        string endpoint = Required("AzureOpenAI:Endpoint");
        string apiKey = Required("AzureOpenAI:ApiKey");

        // Its own deployment, not the agent's. A model grading its own output marks
        // it generously, so the instrument and the thing it measures do not share one.
        string deployment = Required("AzureOpenAI:JudgeDeployment");

        string Required(string key) => config[key]
            ?? throw new InvalidOperationException(
                $"{key} is missing. Confirm this project's <UserSecretsId> matches the "
                + "agent's, or run 'dotnet user-secrets list' against the agent.");

        // The stored endpoint is the bare resource root. The agent builds the v1
        // path itself, and so do we. Keep this identical to the agent's code.
        Uri apiBase = new UriBuilder(endpoint) { Path = "openai/v1/" }.Uri;

        OpenAIClient client = new(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = apiBase });

        return client.GetChatClient(deployment)
            .AsIChatClient()
            .AsBuilder()
            .ConfigureOptions(o =>
                // Pinned, and pinned above None. Grading against a rubric is the work
                // reasoning budget is for, and an unset effort drifts under you.
                o.Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Medium })
            .Build();
    }
}
