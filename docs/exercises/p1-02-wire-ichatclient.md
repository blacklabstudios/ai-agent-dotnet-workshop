# P1.02 - Wire IChatClient

> Pillar 1, Part 2. Individual.

## Mission

Add the AI packages, register `IChatClient` in a DI container, and replace the echo line in `Program.cs` with a real call to your `gpt-5.6-luna` deployment.

By the end, typing "hello" in the REPL prints a sentence from your Azure deployment.

**Learning Objectives**:

- Use `IChatClient` to keep the chat provider behind one interface
- Register a model client through a `ServiceCollection`
- Call Azure OpenAI through the OpenAI SDK's v1-compatible endpoint
- Set a client-wide reasoning default with `ChatClientBuilder`
- Stream a reply with `GetStreamingResponseAsync` (Extra)

---

## Prerequisites

- P1.01 finished (Azure resource, three secrets in `dotnet user-secrets`, Foundry playground replied to "hello", and P1.01's Step 5.4 `curl` came back with JSON)
- The Postgres + pgvector container is running. From the repo root: `docker compose up -d`. Verify with `docker compose ps`.
- The starter's REPL still echoes (you haven't done P1.02 yet)

---

## What we're solving

The starter REPL reads input and echoes it back. There is no model behind it yet.

Before "hello" gets a real answer, add three things:

1. **The packages** that expose `IChatClient` and the OpenAI client implementation.
2. **A registration** that tells DI how to construct an `IChatClient` from your Azure secrets.
3. **A real call inside the loop**: build a list of messages, send it to the chat client, print the response.

---

## If you're comfortable, do this

Use this list if you want the route first. The full steps explain the choices and help you recover when a step fails.

1. Add three package references to `FinanceAssistant.csproj`: `Microsoft.Extensions.AI`, `Microsoft.Extensions.AI.OpenAI`, `Microsoft.Extensions.DependencyInjection`.
2. Create `src/FinanceAssistant/ServiceCollectionExtensions.cs` with an `AddChatClient` extension method that registers `IChatClient` against your Azure deployment via the OpenAI SDK's Azure v1 endpoint, pinning reasoning effort to `None` through `ConfigureOptions`.
3. Update `Program.cs`: add three `using` lines, build a `ServiceCollection`, resolve `IChatClient`, replace the echo with a real call (system prompt plus user input, into `GetResponseAsync`, print the text).
4. Run. Type "hello". Confirm a sentence from your model.

Two Extras sit after Step 4: streaming the reply, and turning the reasoning dial up to feel what `None` bought you. Both are optional and both get reverted. Nothing in P2.01 depends on either.

---

## Step 1: Add the AI packages

Open `src/FinanceAssistant/FinanceAssistant.csproj`. Inside the existing `<ItemGroup>` that already lists `CsvHelper`, the EF Core packages, and the configuration packages, add three more lines:

```xml
<PackageReference Include="Microsoft.Extensions.AI" Version="10.9.0" />
<PackageReference Include="Microsoft.Extensions.AI.OpenAI" Version="10.9.0" />
<PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.11" />
```

Verify:

```bash
dotnet build
```

Should still build with zero errors. The OpenAI SDK comes in transitively through `Microsoft.Extensions.AI.OpenAI`. You don't need to reference it directly.

---

## Step 2: Create ServiceCollectionExtensions.cs

`ServiceCollectionExtensions.cs` holds a static class with an extension method on `IServiceCollection`. The method registers `IChatClient` against your Azure deployment.

Create `src/FinanceAssistant/ServiceCollectionExtensions.cs`:

```csharp
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

        // /openai/v1/ is the OpenAI SDK's Azure v1-compatible surface.
        var apiBase = new UriBuilder(endpoint) { Path = "openai/v1/" }.Uri;

        return services.AddSingleton<IChatClient>(_ =>
            new OpenAIClient(
                    new ApiKeyCredential(apiKey),
                    new OpenAIClientOptions { Endpoint = apiBase })
                .GetChatClient(deployment)
                .AsIChatClient()
                .AsBuilder()
                .ConfigureOptions(o =>
                    o.Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None })
                .Build());
    }
}
```

> **What those last four lines do.** `gpt-5.6-luna` is a reasoning model. Left alone it defaults to medium effort: before answering, it spends tokens and time thinking. Useful for hard problems, and the wrong default for a workshop where you want to type `hello` and get a reply.
>
> `ReasoningEffort.None` turns that off. The knob has six positions on this model (none, low, medium, high, xhigh, max) and you just set it to the bottom one, on purpose. Try `Medium` later and watch the latency change. That's the lesson: reasoning is a dial you own, not a property of the model you're stuck with.
>
> `ConfigureOptions` sets it once, here, at the point the client is registered. Every call for the rest of the workshop inherits it, including the ones you haven't written yet. It clones whatever options the caller passes, so when Pillar 2 starts handing tools to `GetResponseAsync`, the tools and the reasoning setting both survive.

> **Why `AddSingleton` and not `AddScoped`?** The chat client is a thin wrapper over an `HttpClient` that is thread-safe and stateless. Every request carries its own messages, so there's nothing per-request to keep separate. Constructing a new client (and its socket pool) for each turn would be wasteful. In ASP.NET apps you'll often see `AddScoped` as the default for "things created per request", but for an HTTP client like this, singleton is the right call.

> **The name is already taken, and yours wins.** `Microsoft.Extensions.AI` ships its own `AddChatClient` extension, in the `Microsoft.Extensions.DependencyInjection` namespace, which is the namespace Step 3.1 has you import. Both are in scope on the same line and it compiles cleanly, because the library's overloads take an `IChatClient` or a `Func<IServiceProvider, IChatClient>` and neither of those accepts an `IConfiguration`. Worth knowing anyway: `services.AddChatClient(someChatClient)` would quietly resolve to the library's version instead of yours, and that registration has no reasoning effort pinned on it.

Verify:

```bash
dotnet build
```

Zero errors again. Every compile error the Troubleshooting section anticipates for this file surfaces here if it's going to, rather than three edits into the next step.

---

## Step 3: Update Program.cs

Three discrete edits on top of the existing file. The starting state has a `using FinanceAssistant.Data;` line, a `using Microsoft.Extensions.Configuration;` line, the EF Core block, the `ConfigurationBuilder` block, and an echo loop.

### 3.1: Add three usings

At the top of `Program.cs`, alongside the two `using` lines already there, add:

```csharp
using FinanceAssistant;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
```

### 3.2: Build the service collection and resolve IChatClient

Right after the `var config = new ConfigurationBuilder()...Build();` block, before the `Console.WriteLine("Finance assistant. ...")` line, add this block:

```csharp
var services = new ServiceCollection();
services.AddChatClient(config);
var provider = services.BuildServiceProvider();

var chatClient = provider.GetRequiredService<IChatClient>();

var systemPrompt = await File.ReadAllTextAsync(
    Path.Combine(AppContext.BaseDirectory, "Prompts", "SystemPrompt.md"));
```

> `AddChatClient` is the only line in `Program.cs` that knows about the AI registration. Everything below uses the `IChatClient` abstraction. If you ever want to put the chat client behind a different provider, the change lives in `ServiceCollectionExtensions.cs`, not here.

### 3.3: Replace the echo line with a real call

Find the `Console.WriteLine($"(echo) {input}");` line inside the `while` loop. Replace it with:

```csharp
var messages = new List<ChatMessage>
{
    new(ChatRole.System, systemPrompt),
    new(ChatRole.User, input)
};

var response = await chatClient.GetResponseAsync(messages);
Console.WriteLine(response.Text);
```

The full `while` block now reads:

```csharp
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

    var response = await chatClient.GetResponseAsync(messages);
    Console.WriteLine(response.Text);
}
```

> `response.Text` is the convenient accessor for the assistant's reply. There's also `response.Messages` if you want the full structured reply, but for this exercise the text is what we want.

---

## Step 4: Run

From the repo root:

```bash
dotnet run --project src/FinanceAssistant
```

Type `hello`. You should get a sentence back from your `gpt-5.6-luna` deployment. Type `exit` to quit.

If you do, the wiring is correct.

---

## Extra: stream the response

`GetResponseAsync` waits. The model generates the whole answer, the SDK hands you a finished object, and only then does anything reach the screen. On a one-line reply you barely notice. On a paragraph you sit and watch a blank prompt.

Streaming doesn't make the answer faster. It takes exactly as long to finish either way. What changes is *when your code receives the first fragment*, which is not always the same moment the model produced it. That gap is the point of this Extra.

### The change

In `Program.cs`, find the two lines from Step 3.3:

```csharp
var response = await chatClient.GetResponseAsync(messages);
Console.WriteLine(response.Text);
```

Replace them with:

```csharp
await foreach (var update in chatClient.GetStreamingResponseAsync(messages))
{
    Console.Write(update.Text);
}

Console.WriteLine();
```

Run again, and ask for something long enough to see what happens:

```
> explain compound interest to me in three paragraphs
```

### What you actually saw

One of two things, and which one you get is a property of your deployment, not of your code.

**The text arrives in pieces, as the model writes it.** This is what streaming is for, and there's nothing more to say about it.

**Or the prompt sits blank for several seconds, and then the whole answer appears at once.** Your loop still received hundreds of separate fragments. They just all arrived inside the same fraction of a second, at the end. Azure's default content filter buffers the completion, filters it, and only then releases the stream, so the tokens leave the model progressively and reach you in one burst. The deployment setting that changes this is the content filter's streaming mode: Asynchronous Filter releases as it goes, the default does not.

Either way your code is correct and the API is identical. That's worth more than the animation. Streaming is a property of the whole path, from the model through the content filter through the SDK to your console, and the one link you control is not the link that decides what the user sees.

### What you got back

`GetStreamingResponseAsync` returns an `IAsyncEnumerable<ChatResponseUpdate>`. A `ChatResponseUpdate` is a fragment, not a message. Its `.Text` is whatever arrived in that chunk, often a few characters. Concatenate every fragment and you have the same string `response.Text` handed you a moment ago.

Note `Console.Write`, not `Console.WriteLine`. Each fragment is a piece of one sentence, so a newline per fragment shreds the output. The single `Console.WriteLine()` after the loop closes the line once the stream ends.

### The part that bites later

The loop above prints the fragments and throws them away. Fine for one turn. Not fine from Pillar 3 on, where the agent loop has to put the assistant's own messages back into history before it can carry on.

One method fixes it:

```csharp
List<ChatResponseUpdate> updates = [];

await foreach (var update in chatClient.GetStreamingResponseAsync(messages))
{
    Console.Write(update.Text);
    updates.Add(update);
}

Console.WriteLine();

var response = updates.ToChatResponse();
Console.WriteLine($"[{response.Messages.Count} message(s) to put back into history]");
```

`ToChatResponse()` collapses the fragments back into the same `ChatResponse` the non-streaming call would have returned, `response.Messages` and all. Streaming is a rendering choice. It's not a different conversation.

The `Console.WriteLine` on the end is only there so you can watch that happen. Delete it once you have.

### Then put it back

P2.01 onwards is written against `GetResponseAsync`, and the `p1-02-end` branch keeps the non-streaming version. Put the two lines from Step 3.3 back before you move on, or keep streaming and expect to adapt every later step yourself.

Streaming with tools in the loop is a bigger job than it looks. The fragments carry `FunctionCallContent` as well as text, so "print what arrives" has to learn the difference between a word and a tool call. That's real work, and it isn't Pillar 1's.

---

## Extra: turn the reasoning dial up

Step 2 pinned reasoning effort to `None` and claimed it was a dial you own. Turn it and find out.

In `ServiceCollectionExtensions.cs`, change one word:

```csharp
.ConfigureOptions(o =>
    o.Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Medium })
```

Run, and ask something that rewards thinking:

```
> I earn 4200 EUR a month, rent is 1500, and I want 20000 saved in three years. Is that realistic?
```

Then set it back to `None` and ask the same question again.

One thing moves reliably: the wait before the first word gets longer, because the model is spending tokens before it writes any. On this question, over three runs each, `None` answered in about 2.8 seconds and `Medium` in about 4.4. Take `ConfigureOptions` out of the registration entirely and you land near 4.2, which is the medium default Step 2 warned you about.

What often doesn't move is the answer. At this size both settings tend to come back with the same breakdown and the same numbers. That's the useful result rather than a disappointing one: you paid roughly 60% more latency for no visible gain, which is exactly why the dial is a per-feature decision instead of a global one. Ask something genuinely harder and the trade starts to pay.

If you did the streaming Extra too, run them together. If your deployment streams progressively, the reasoning pause becomes visible: at `None` the text starts almost immediately, at `Medium` you watch nothing happen and then the answer arrives quickly once it starts. If your deployment buffers, you will see the same wait grow without the animation.

> **What the enum actually offers.** `ReasoningEffort` in `Microsoft.Extensions.AI` 10.9.0 is a five-value enum: `None`, `Low`, `Medium`, `High`, `ExtraHigh`. `gpt-5.6-luna` exposes a sixth level above those, and there's no enum member for it. Reaching it means going around `IChatClient` to the provider's own options. That's the standing cost of a portable abstraction: it lags the newest knob on any one provider. You accepted that trade the moment you typed `IChatClient`, and it's worth knowing you accepted it.

Set it back to `None` before P2.01. Every exercise after this one is written and timed against the fast setting.

---

## Troubleshooting

### `Missing AzureOpenAI:...` thrown at startup

Your secrets aren't being read. You want to confirm the three Azure keys exist without printing their values into your terminal (handy if you're screen-sharing or pasting output into chat).

From `src/FinanceAssistant/`, run one of these.

On macOS/Linux (bash/zsh):

```bash
# Just the key names - values are masked.
dotnet user-secrets list | awk -F' = ' '{print $1}'

# Or: just count that all three keys are present.
dotnet user-secrets list | grep -c '^AzureOpenAI:'   # expect 3
```

On Windows (PowerShell):

```powershell
# Just the key names - values are masked.
dotnet user-secrets list | ForEach-Object { ($_ -split ' = ')[0] }

# Or: just count that all three keys are present.
(dotnet user-secrets list | Select-String '^AzureOpenAI:').Count   # expect 3
```

If you see fewer than three `AzureOpenAI:*` keys, you set them in the wrong folder (the user-secrets store is keyed off the project's `UserSecretsId`). Re-run the `dotnet user-secrets set` commands from `src/FinanceAssistant/`.

If a value looks wrong, prefer re-setting it with `dotnet user-secrets set` over reading it back with `dotnet user-secrets get` (the `get` command prints the secret in plaintext).

### `401 Unauthorized` from Azure

Either your key is wrong or your endpoint is wrong. Both come from the same resource's "Keys and Endpoint" pane in the Azure portal. Re-copy together.

### `404 Not Found` from Azure

The deployment name doesn't match. Compare your stored deployment value against the name in Foundry's "Models + endpoints", character-for-character. To print just that one secret without dumping the rest:

```bash
# macOS/Linux
dotnet user-secrets list | grep '^AzureOpenAI:Deployment'
```

```powershell
# Windows (PowerShell)
dotnet user-secrets list | Select-String '^AzureOpenAI:Deployment'
```

If the deployment name checks out, it's the endpoint pointing at the wrong resource, not the endpoint's formatting. `UriBuilder` sets the path rather than appending to it, so `https://your-resource.openai.azure.com` and `https://your-resource.openai.azure.com/` both produce `https://your-resource.openai.azure.com/openai/v1/chat/completions`. A missing trailing slash is not the cause of a 404 here, whatever older Azure tutorials say. The same property means a paste of the old full deployment URL, `api-version` query string and all, still lands on the right endpoint.

The one paste it can't save you from is a missing scheme. `your-resource.openai.azure.com` without the leading `https://` becomes `http://your-resource.openai.azure.com/openai/v1/chat/completions`, in plaintext. Azure's edge accepts that request on port 80 the same as it does on 443, so it doesn't fail loudly: it comes back as a 404, identical to the wrong-deployment-name case above. The error message won't tell you which mistake you made, so if the deployment name and the resource both check out, look at the scheme next. Copy the endpoint out of the portal rather than retyping it, so the scheme never goes missing in the first place.

### `AsIChatClient()` is not found

The `Microsoft.Extensions.AI.OpenAI` package isn't pulled in. Confirm `FinanceAssistant.csproj` references it, then run `dotnet clean` and `dotnet build` again.

### `OpenAIClient`, `ApiKeyCredential`, or `OpenAIClientOptions` is not found

`Microsoft.Extensions.AI.OpenAI` brings the OpenAI SDK in transitively, but the IDE may need `using OpenAI;` and `using System.ClientModel;` to see them. Both are at the top of `ServiceCollectionExtensions.cs` as written above.

---

## You can now

Type questions into the REPL and get real responses from `gpt-5.6-luna`.

The model has no tools yet, so factual questions about your finances will produce guesses. The system prompt that shapes the assistant's tone lives in `Prompts/SystemPrompt.md`. Edit that file, restart the REPL, and watch the assistant's personality shift. That's prompt-as-a-workflow in its smallest form.

---

## Summary

You've added:

- **Three packages**: `Microsoft.Extensions.AI`, `Microsoft.Extensions.AI.OpenAI`, and `Microsoft.Extensions.DependencyInjection`.
- **`ServiceCollectionExtensions.cs`**: an extension method that registers `IChatClient` against your Azure deployment via the OpenAI SDK's Azure v1 endpoint.
- **A client-wide default**: `ConfigureOptions` pins reasoning effort to `None`. Every call for the rest of the workshop inherits it, and Pillar 2 adds a second stage to the same pipeline.
- **Updated `Program.cs`**: three new `using`s, a `ServiceCollection`, the DI resolution, the `IChatClient` call inside the loop.
- **A working agent**: type a question, get a real reply.
- **Two Extras, if you took them**: a streamed reply, and a feel for what `None` costs and buys. Both reverted before P2.01.

---

## What's next

P2.01 is your first tool: a `ConvertCurrency` function with a hardcoded rate table. No DB, no embeddings. The point is to feel what `[Description]` quality means before the bigger transactions exercise lands later in the afternoon.

---

## Additional Resources

- [Microsoft.Extensions.AI documentation](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)
- [OpenAI .NET SDK on GitHub](https://github.com/openai/openai-dotnet)
- [Azure OpenAI v1 API surface](https://learn.microsoft.com/en-us/azure/foundry/openai/api-version-lifecycle)
