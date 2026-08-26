using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;

namespace FinanceAssistant;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddChatClient(this IServiceCollection services, IConfiguration config)
    {
        var endpoint = config["AzureOpenAI:Endpoint"]
            ?? throw new InvalidOperationException("Missing AzureOpenAI:Endpoint.");
        var apiKey = config["AzureOpenAI:ApiKey"]
            ?? throw new InvalidOperationException("Missing AzureOpenAI:ApiKey.");
        var deployment = config["AzureOpenAI:Deployment"]
            ?? throw new InvalidOperationException("Missing AzureOpenAI:Deployment.");
        var reasoningEffort = Enum.Parse<ReasoningEffort>(
            config["AzureOpenAI:ReasoningEffort"]
                ?? throw new InvalidOperationException("Missing AzureOpenAI:ReasoningEffort."),
            ignoreCase: true);

        // The endpoint is the full v1 base URL: Foundry's /openai/v1/ or OpenAI's /v1/.
        // The trailing slash is normalised, so a paste without one still works.
        var apiBase = new Uri(endpoint.TrimEnd('/') + "/");

        return services.AddSingleton<IChatClient>(_ =>
            new OpenAIClient(
                    new ApiKeyCredential(apiKey),
                    new OpenAIClientOptions { Endpoint = apiBase })
                .GetChatClient(deployment)
                .AsIChatClient()
                .AsBuilder()
                .ConfigureOptions(o =>
                    o.Reasoning = new ReasoningOptions { Effort = reasoningEffort })
                .Build());
    }
}
