using FinanceAssistant.Data;
using Microsoft.Extensions.Configuration;


var config = new ConfigurationBuilder()
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();

if (!await FinanceAssistant.StartupCheck.RunAsync(config))
{
    return 1;
}

await using (var db = new FinanceDbContext())
{
    await db.Database.EnsureCreatedAsync();
}

Console.WriteLine("Finance assistant. Type a message, or 'exit' to quit.");

while (true)
{
    Console.Write("> ");
    var input = Console.ReadLine();
    if (input is null || string.Equals(input.Trim(), "exit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    Console.WriteLine($"(echo) {input}");
}

return 0;
