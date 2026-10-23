using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.AI;

namespace FinanceAssistant;

// M.E.AI's gen_ai.client.token.usage histogram answers "how big is a typical call".
// These counters answer "how much have I spent, and on which model".
public static class LlmCostTelemetry
{
    private const string CostTag = "finance_assistant.cost.usd";

    // USD per million tokens, from the price list in P1.01. Prices change, so check yours.
    private static readonly Dictionary<string, ModelPrice> Prices = new()
    {
        ["gpt-5.6-luna"] = new(Input: 0.20, CachedInput: 0.02, Output: 1.20),
        ["gpt-5.6-sol"] = new(Input: 4.00, CachedInput: 0.40, Output: 20.00),
        ["gpt-5-mini"] = new(Input: 0.25, CachedInput: 0.025, Output: 2.00),
        ["text-embedding-3-small"] = new(Input: 0.02, CachedInput: 0.02, Output: 0),
    };

    private static readonly Meter Meter = new(ChatAgent.TelemetrySourceName);

    private static readonly Counter<long> Tokens = Meter.CreateCounter<long>(
        "finance_assistant.llm.tokens",
        unit: "{token}",
        description: "Tokens billed, split into uncached input, cached input and output.");

    private static readonly Counter<double> Cost = Meter.CreateCounter<double>(
        "finance_assistant.llm.cost",
        unit: "{dollar}",
        description: "Estimated spend in USD, from token usage and list prices.");

    private static readonly HashSet<string> UnpricedModels = [];

    public static ChatClientBuilder UseCostTracking(this ChatClientBuilder builder) =>
        builder.Use(
            getResponseFunc: async (messages, options, inner, ct) =>
            {
                var response = await inner.GetResponseAsync(messages, options, ct);
                var model = response.ModelId ?? inner.GetService<ChatClientMetadata>()?.DefaultModelId;
                Record("chat", model, response.Usage);
                return response;
            },
            getStreamingResponseFunc: null);

    public static EmbeddingGeneratorBuilder<TInput, TEmbedding> UseCostTracking<TInput, TEmbedding>(
        this EmbeddingGeneratorBuilder<TInput, TEmbedding> builder)
        where TEmbedding : Embedding =>
        builder.Use(async (values, options, inner, ct) =>
        {
            var embeddings = await inner.GenerateAsync(values, options, ct);
            // Embedding responses don't name the model, so fall back to the deployment name.
            var model = inner.GetService<EmbeddingGeneratorMetadata>()?.DefaultModelId;
            Record("embeddings", model, embeddings.Usage);
            return embeddings;
        });

    private static void Record(string operation, string? model, UsageDetails? usage)
    {
        if (model is null || usage is null)
        {
            return;
        }

        // Cached tokens are part of the input count, but billed at a discount. Split them out
        // so the three token types don't overlap and add up to the total.
        var cachedInput = usage.CachedInputTokenCount ?? 0;
        var input = (usage.InputTokenCount ?? 0) - cachedInput;
        var output = usage.OutputTokenCount ?? 0;

        // The response names a dated snapshot, like gpt-5.6-luna-2026-07-09. Match on the prefix.
        var price = Prices.FirstOrDefault(p => model.StartsWith(p.Key, StringComparison.OrdinalIgnoreCase)).Value;
        if (price is null && UnpricedModels.Add(model))
        {
            Console.Error.WriteLine($"[cost] no price for '{model}'. Tokens are counted, cost is not.");
        }

        var cost = 0.0;
        cost += Add("input", input, price?.Input);
        cost += Add("cached_input", cachedInput, price?.CachedInput);
        cost += Add("output", output, price?.Output);

        if (price is null)
        {
            return;
        }

        // This runs inside UseOpenTelemetry(), so the current activity is this call's span.
        Activity.Current?.SetTag(CostTag, cost);

        // Add the call's cost to the turn, so each invoke_agent span says what the question cost.
        for (var turn = Activity.Current?.Parent; turn is not null; turn = turn.Parent)
        {
            if (turn.Source.Name == ChatAgent.TelemetrySourceName)
            {
                turn.SetTag(CostTag, (turn.GetTagItem(CostTag) as double? ?? 0) + cost);
                break;
            }
        }

        double Add(string tokenType, long count, double? pricePerMillion)
        {
            var tags = new TagList
            {
                { "gen_ai.operation.name", operation },
                { "gen_ai.response.model", model },
                { "finance_assistant.token.type", tokenType },
            };

            Tokens.Add(count, tags);
            if (pricePerMillion is null)
            {
                return 0;
            }

            var amount = count * pricePerMillion.Value / 1_000_000;
            Cost.Add(amount, tags);
            return amount;
        }
    }

    private sealed record ModelPrice(double Input, double CachedInput, double Output);
}
