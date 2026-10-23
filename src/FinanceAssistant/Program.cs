using FinanceAssistant;
using FinanceAssistant.Data;
using FinanceAssistant.Memory;
using FinanceAssistant.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Pgvector;

// M.E.AI's default name for the spans and metrics that UseOpenTelemetry() emits.
// "Experimental" because the GenAI semantic conventions are still in development.
const string MeaiTelemetrySourceName = "Experimental.Microsoft.Extensions.AI";

var resource = ResourceBuilder.CreateDefault().AddService("finance-assistant");

// Both exporters default to OTLP over gRPC on localhost:4317.
// Point them elsewhere with OTEL_EXPORTER_OTLP_ENDPOINT, no code change.
using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .SetResourceBuilder(resource)
    .AddSource(MeaiTelemetrySourceName)
    .AddSource(ChatAgent.TelemetrySourceName)
    .AddOtlpExporter()
    .Build();

using var meterProvider = Sdk.CreateMeterProviderBuilder()
    .SetResourceBuilder(resource)
    .AddMeter(MeaiTelemetrySourceName)
    .AddMeter(ChatAgent.TelemetrySourceName)
    // The default export interval is 60 seconds. Five keeps the dashboard live during a demo.
    .AddOtlpExporter((_, reader) =>
        reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 5_000)
    .Build();

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

var convertCurrency = new ConvertCurrencyTool();
var getTransactions = new GetTransactionsTool();
var searchTransactions = new SearchTransactionsTool(embedder);
var transferFunds = new TransferFundsTool();

var chatOptions = new ChatOptions
{
    Tools =
    [
        AIFunctionFactory.Create(convertCurrency.Convert),
        AIFunctionFactory.Create(getTransactions.GetTransactions),
        AIFunctionFactory.Create(searchTransactions.SearchTransactions),
        new ApprovalRequiredAIFunction(AIFunctionFactory.Create(transferFunds.Transfer))
    ]
};

var systemPrompt = await File.ReadAllTextAsync(
    Path.Combine(AppContext.BaseDirectory, "Prompts", "SystemPrompt.md"));

var store = new ConversationStore();
var reducer = new SummarizingHistoryReducer(chatClient);
var chatAgent = new ChatAgent(chatClient, chatOptions, store, systemPrompt, reducer);

Console.WriteLine("Finance assistant. Type a message, or 'exit' to quit.");

while (true)
{
    Console.Write("> ");
    var input = Console.ReadLine();
    if (input is null || string.Equals(input.Trim(), "exit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    Console.WriteLine($"[memory] {store.Messages.Count} messages in history");

    var reply = await chatAgent.RunTurnAsync(input);
    Console.WriteLine(reply);
}

return 0;
