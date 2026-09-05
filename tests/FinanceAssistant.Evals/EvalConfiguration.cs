using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;

namespace FinanceAssistant.Evals;

// One config load for the whole suite. User-secrets for local work, environment
// variables for CI. Same keys either way.
internal static class EvalConfiguration
{
    private static readonly IConfiguration Configuration =
        new ConfigurationBuilder()
            .AddUserSecrets(typeof(EvalConfiguration).Assembly, optional: true)
            .AddEnvironmentVariables()
            .Build();

    public static IConfiguration Load() => Configuration;

    // All five keys, not three. JudgeDeployment is its own deployment, so a lane that
    // exported the agent's three and stopped would throw inside Judge rather than skip.
    // EmbeddingDeployment is the same story one layer along: every eval that touches
    // AgentUnderTest goes through a registration of the embedding generator, and that
    // registration reads the key eagerly. Check only the obvious keys and a half-configured
    // lane explodes instead of skipping, which is the failure this attribute exists to prevent.
    public static bool HasCredentials =>
        !string.IsNullOrWhiteSpace(Configuration["AzureOpenAI:Endpoint"])
        && !string.IsNullOrWhiteSpace(Configuration["AzureOpenAI:ApiKey"])
        && !string.IsNullOrWhiteSpace(Configuration["AzureOpenAI:Deployment"])
        && !string.IsNullOrWhiteSpace(Configuration["AzureOpenAI:JudgeDeployment"])
        && !string.IsNullOrWhiteSpace(Configuration["AzureOpenAI:EmbeddingDeployment"]);
}

// Every eval in this project calls a live model and bills tokens for it. On a machine or
// a CI lane with no credentials we skip rather than throw: a missing key is a missing key,
// not a regression in the agent, and a suite that explodes on checkout gets deleted by the
// third person who hits it.
//
// SkipUnless names a public static bool. xUnit v3 reads it when the test RUNS, not when
// this constructor runs, which is the whole reason to write it this way.
public sealed class EvalFactAttribute : FactAttribute
{
    public EvalFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
            : base(sourceFilePath, sourceLineNumber)
    {
        SkipUnless = nameof(EvalConfiguration.HasCredentials);
        SkipType = typeof(EvalConfiguration);
        // All five names, matching HasCredentials. Name four and a lane that exported
        // exactly those four skips everything while the message lists what it already set.
        Skip = "No Azure OpenAI credentials. Set user-secrets against the "
             + "'finance-assistant-workshop' id, or AzureOpenAI__Endpoint, "
             + "AzureOpenAI__ApiKey, AzureOpenAI__Deployment, "
             + "AzureOpenAI__JudgeDeployment and "
             + "AzureOpenAI__EmbeddingDeployment in the environment.";
    }
}
