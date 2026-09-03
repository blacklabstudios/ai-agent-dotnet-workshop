using FinanceAssistant;
using FinanceAssistant.McpServer;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables();

builder.Services.AddEmbeddingGenerator(builder.Configuration);

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<McpTools>()
    .WithPrompts<McpPrompts>()
    .WithResources<McpResources>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .AllowAnyOrigin()
        .AllowAnyHeader()
        .AllowAnyMethod());
});

var app = builder.Build();
app.UseCors();
app.MapMcp();
app.Run("http://localhost:5050");
