using FinanceAssistant;
using FinanceAssistant.Data;
using FinanceAssistant.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;


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
var provider = services.BuildServiceProvider();

var chatClient = provider.GetRequiredService<IChatClient>();

var convertCurrency = new ConvertCurrencyTool();
var chatOptions = new ChatOptions
{
    Tools = [AIFunctionFactory.Create(convertCurrency.Convert)]
};

var systemPrompt = await File.ReadAllTextAsync(
    Path.Combine(AppContext.BaseDirectory, "Prompts", "SystemPrompt.md"));

Console.WriteLine("Finance assistant. Type a message, or 'exit' to quit.");

while (true)
{
    Console.Write("> ");
    var input = Console.ReadLine();
    if (input is null || string.Equals(input.Trim(), "exit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    var messages = new List<ChatMessage>
    {
        new(ChatRole.System, systemPrompt),
        new(ChatRole.User, input)
    };

    var response = await chatClient.GetResponseAsync(messages, chatOptions);
    Console.WriteLine(response.Text);
}

return 0;
