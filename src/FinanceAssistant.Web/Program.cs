using AGUI.Abstractions;
using AGUI.Server;
using FinanceAssistant;
using FinanceAssistant.Data;
using FinanceAssistant.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Pgvector;

using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddChatClient(builder.Configuration);
builder.Services.AddEmbeddingGenerator(builder.Configuration);

// AG-UI first, and AGUIJsonUtilities.DefaultTypeInfoResolver rather than the bare
// source-generated context. TypedResults.ServerSentEvents serialises with these application
// options, so two things have to hold for a field with no value to stay off the wire: the
// AG-UI resolver has to carry the omission, and it has to be asked before AIJsonUtilities'
// resolver, which answers for any type and would otherwise resolve AG-UI events itself.
builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AGUIJsonUtilities.DefaultTypeInfoResolver);
    options.SerializerOptions.TypeInfoResolverChain.Add(AIJsonUtilities.DefaultOptions.TypeInfoResolver!);
    AGUIJsonUtilities.RegisterInterruptContentTypes(options.SerializerOptions);
});

var systemPrompt = await File.ReadAllTextAsync(
    Path.Combine(AppContext.BaseDirectory, "Prompts", "SystemPrompt.md"));

builder.Services.AddSingleton(sp =>
{
    var embedder = sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
    var chatOptions = new ChatOptions { Tools = AgentToolset.CreateTools(embedder) };

    return new SessionRegistry(sp.GetRequiredService<IChatClient>(), chatOptions, systemPrompt);
});

var app = builder.Build();

// Same startup the console app runs: create the schema if it isn't there,
// then embed any transaction that doesn't have a vector yet.
await using (var db = new FinanceDbContext())
{
    await db.Database.EnsureCreatedAsync();

    var unembedded = await db.Transactions.Where(t => t.Embedding == null).ToListAsync();
    if (unembedded.Count > 0)
    {
        app.Logger.LogInformation("Embedding {Count} transactions...", unembedded.Count);
        var texts = unembedded.Select(t => $"{t.Merchant} {t.Description}").ToList();
        var embedder = app.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        var embeddings = await embedder.GenerateAsync(texts);
        for (var i = 0; i < unembedded.Count; i++)
        {
            unembedded[i].Embedding = new Vector(embeddings[i].Vector.ToArray());
        }
        await db.SaveChangesAsync();
    }
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/chat", (
    [FromBody] RunAgentInput input,
    SessionRegistry sessions,
    IOptions<JsonOptions> jsonOptions,
    CancellationToken ct) =>
{
    var context = input.ToChatRequestContext(jsonOptions.Value.SerializerOptions);
    var session = sessions.GetOrCreate(input.ThreadId);

    var events = session.RunAsync(context, ct).AsAGUIEventStreamAsync(context, ct);

    return TypedResults.ServerSentEvents(events);
});

app.Run("http://localhost:5080");
