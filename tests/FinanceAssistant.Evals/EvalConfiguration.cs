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
}
