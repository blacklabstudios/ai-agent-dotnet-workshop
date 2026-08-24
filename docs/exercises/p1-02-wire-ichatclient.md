# P1.02 - Wire IChatClient

> Pillar 1, Part 2. Individual.

## Mission

Add the AI packages, register `IChatClient` in a DI container, and replace the echo line in `Program.cs` with a real call to your `chat` deployment.

By the end, typing "hello" in the REPL prints a sentence from your Azure deployment.

**Learning Objectives**:

- Use `IChatClient` to keep the chat provider behind one interface
- Register a model client through a `ServiceCollection`
- Call Azure OpenAI through the OpenAI SDK's v1-compatible endpoint
- Set a client-wide reasoning default with `ChatClientBuilder`
- Stream a reply with `GetStreamingResponseAsync` (Extra)

---

## At a glance

**You'll leave with**

- `IChatClient` keeps the provider behind one interface. Swapping it touches only `ServiceCollectionExtensions.cs`.
- Register the client as a singleton in a `ServiceCollection`, and resolve it below the startup check.
- `ConfigureOptions` pins reasoning effort once, and every later call inherits it.
- Reasoning is a dial you own. Turning it up costs latency and often buys no better answer.
- The catch: on Path A, set reasoning back to `None` before P2.01, or every tool request is rejected.

**The flow**

You · *hello* → Program.cs · *system prompt plus input* → **IChatClient** · *GetResponseAsync* → Pipeline · *pins reasoning effort* → Chat deployment · *your path's model* → Reply · *a real sentence*

**Test it**

```text
$ dotnet run --project src/FinanceAssistant
Checking your setup...
  [ok] Secrets: Endpoint, ApiKey, Deployment and ReasoningEffort are set.
  [ok] Database: Postgres is answering on localhost:5432.
  [ok] Chat deployment "chat" answered, running gpt-5.6-luna-2026-07-09.
Finance assistant. Type a message, or 'exit' to quit.
> hello
```

Look for: every check line still at `[ok]`, then a sentence back from your `chat` deployment after `hello`. The reply's wording is the model's own and will differ, and on Path B the third line names `gpt-5-mini`.

**Your agent so far**

- ✓ Has a model deployed and its keys stored locally
- ✓ Answers from a real model
- ✗ Calls a tool you wrote

## Prerequisites

- P1.01 finished (a path picked, Azure resource, four secrets in `dotnet user-secrets`, Foundry playground replied to "hello")
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
2. Create `src/FinanceAssistant/ServiceCollectionExtensions.cs` with an `AddChatClient` extension method that registers `IChatClient` against your Azure deployment via the OpenAI SDK's Azure v1 endpoint, pinning reasoning effort to your `AzureOpenAI:ReasoningEffort` secret through `ConfigureOptions`.
3. Update `Program.cs`: add three `using` lines, build a `ServiceCollection`, resolve `IChatClient`, replace the echo with a real call (system prompt plus user input, into `GetResponseAsync`, print the text).
4. Run. Type "hello". Confirm a sentence from your model.

Two Extras sit after Step 4: streaming the reply, and turning the reasoning dial up to feel what `None` or `Low` bought you. Both are optional and both get reverted. Nothing in P2.01 depends on either.

---

## Step 1: Add the AI packages

Open `src/FinanceAssistant/FinanceAssistant.csproj`. Inside the existing `<ItemGroup>` that already lists `CsvHelper`, the EF Core packages, and the configuration packages, add three more lines:

```xml
<PackageReference Include="Microsoft.Extensions.AI" Version="10.9.0" />
<PackageReference Include="Microsoft.Extensions.AI.OpenAI" Version="10.9.0" />
<PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.11" />
```

Verify, from the repo root:

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
```

> **What those last four lines do.** `AsBuilder`, `ConfigureOptions`, the `o.Reasoning` line inside it, and `Build` wrap the chat client in a small pipeline that sets reasoning effort on every call. Both workshop models are reasoning models. Left alone they default to medium effort: before answering, they spend tokens and time thinking. Useful for hard problems, and the wrong default for a workshop where you want to type `hello` and get a reply.
>
> So you turn the dial to the bottom, on purpose. Where the bottom is depends on the model, which is why the value comes from your secrets rather than from code. On Path A, `gpt-5.6-luna` has six positions (none, low, medium, high, xhigh, max) and `None` switches thinking off. On Path B, `gpt-5-mini` documents four (minimal, low, medium, high), with no `none` among them. `Low` is the lowest of those the enum can name. `gpt-5-mini` rejects `None`, so Path B stays on `Low`. Try `Medium` later and watch the latency change. That's the lesson: reasoning is a dial you own, not a property of the model you're stuck with.
>
> `Enum.Parse` turns the secret into a `ReasoningEffort`. A typo throws at startup with the bad value in the message, which beats a `400` from the service on your first question.
>
> `ConfigureOptions` sets it once, here, at the point the client is registered. Every call for the rest of the workshop inherits it, including the ones you haven't written yet. It clones whatever options the caller passes, so when Pillar 2 starts handing tools to `GetResponseAsync`, the tools and the reasoning setting both survive. It also sets `o.Reasoning` on every call, so the registration's value wins over any `ChatOptions.Reasoning` a caller passes.

> **Why `AddSingleton` and not `AddScoped`?** The chat client is a thin wrapper over an `HttpClient` that is thread-safe and stateless. Every request carries its own messages, so there's nothing per-request to keep separate. Constructing a new client (and its socket pool) for each turn would be wasteful. In ASP.NET apps you'll often see `AddScoped` as the default for "things created per request", but for an HTTP client like this, singleton is the right call.

> **The name is already taken, and yours wins.** `Microsoft.Extensions.AI` ships its own `AddChatClient` extension, in the `Microsoft.Extensions.DependencyInjection` namespace, which is the namespace Step 3.1 has you import. Both are in scope on the same line and it compiles cleanly, because the library's overloads take an `IChatClient` or a `Func<IServiceProvider, IChatClient>` and neither of those accepts an `IConfiguration`. Worth knowing anyway: `services.AddChatClient(someChatClient)` would quietly resolve to the library's version instead of yours, and that registration has no reasoning effort pinned on it.

Verify:

```bash
dotnet build
```

Zero errors again. Every compile error the Troubleshooting section anticipates for this file surfaces here if it's going to, rather than three edits into the next step.

---

## Step 3: Update Program.cs

Three discrete edits on top of the existing file. The starting state has a `using FinanceAssistant.Data;` line, a `using Microsoft.Extensions.Configuration;` line, the `ConfigurationBuilder` block, the startup check, the EF Core block, and an echo loop.

### 3.1: Add three usings

At the top of `Program.cs`, alongside the two `using` lines already there, add:

```csharp
using FinanceAssistant;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
```

The using block now reads:

```csharp
using FinanceAssistant;
using FinanceAssistant.Data;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
```

With `using FinanceAssistant;` in place, the existing `FinanceAssistant.StartupCheck.RunAsync(config)` line no longer needs its `FinanceAssistant.` prefix. Your IDE may grey it out as redundant. Leave it. It's harmless.

### 3.2: Build the service collection and resolve IChatClient

Right after the EF Core block (`await using (var db = new FinanceDbContext()) { ... }`), before the `Console.WriteLine("Finance assistant. ...")` line, add this block:

```csharp
var services = new ServiceCollection();
services.AddChatClient(config);
var provider = services.BuildServiceProvider();

var chatClient = provider.GetRequiredService<IChatClient>();

var systemPrompt = await File.ReadAllTextAsync(
    Path.Combine(AppContext.BaseDirectory, "Prompts", "SystemPrompt.md"));
```

> It goes below the startup check on purpose. `AddChatClient` throws on a missing secret, and the check explains a missing secret better than the exception does. Put this block above the check and your exception wins.
>
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

Verify:

```bash
dotnet build
```

Zero errors. A typo in the loop shows up here, instead of mixed in with the startup check's output in Step 4.

---

## Step 4: Run

From the repo root:

```bash
dotnet run --project src/FinanceAssistant
```

The startup check runs first, as it did in P1.01. Every line should still say `[ok]`. Then type `hello`. You should get a sentence back from your `chat` deployment. Type `exit` to quit.

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

One of three shapes, and which one you get is a property of your deployment, not of your code.

**The text arrives in pieces, as the model writes it.** This is what streaming is for, and there's nothing more to say about it.

**The text arrives in chunks, a few words or lines at a time.** A burst, a short pause, another burst, until the answer is done. This one is common. Your loop still receives many small fragments. They just reach you in batches.

**Or the prompt sits blank for several seconds, and then the whole answer appears at once.** Your loop still received hundreds of separate fragments. They just all arrived inside the same fraction of a second, at the end.

The chunks and the blank wait most likely have the same cause. Azure's content filter sits between the model and you. The default filter can hold the stream back while it checks the text, and may release it in chunks or all at once. The deployment setting to look at is the content filter's streaming mode. Asynchronous Filter is the one meant to release text as it goes.

Either way your code is correct and the API is identical. That's worth more than the animation. Streaming is a property of the whole path, from the model through the content filter through the SDK to your console, and the one link you control is not the link that decides what the user sees.

### What you got back

`GetStreamingResponseAsync` returns an `IAsyncEnumerable<ChatResponseUpdate>`. A `ChatResponseUpdate` is a fragment, not a message. Its `.Text` is whatever arrived in that chunk, often a few characters. Concatenate every fragment and you have the string `response.Text` would have handed you for this call.

Note `Console.Write`, not `Console.WriteLine`. Each fragment is a piece of one sentence, so a newline per fragment shreds the output. The single `Console.WriteLine()` after the loop closes the line once the stream ends.

### The part that bites later

The loop above prints the fragments and throws them away. Fine for one turn. Not fine from Pillar 3 on, where the agent loop has to put the assistant's own messages back into history before it can carry on.

One method fixes it. This block replaces the loop you wrote in "The change", from `await foreach` to `Console.WriteLine();`:

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

P2.01 onwards is written against `GetResponseAsync`, and the `p1-02-end` branch keeps the non-streaming version.

Doing the reasoning Extra next? Do it before you revert. It suggests running the two Extras together.

To revert, whichever version you have, replace everything from its first line (`List<ChatResponseUpdate> updates` or `await foreach`) up to, but not including, the `while` loop's closing `}` with the two lines from Step 3.3. Do it before you move on, or keep streaming and expect to adapt every later step yourself.

Streaming with tools in the loop is a bigger job than it looks. The fragments carry `FunctionCallContent` as well as text, so "print what arrives" has to learn the difference between a word and a tool call. That's real work, and it isn't Pillar 1's.

---

## Extra: turn the reasoning dial up

Step 2 pinned reasoning effort to the bottom and claimed it was a dial you own. Turn it and find out. Because the value comes from configuration, this is a config change, not a code change.

The quickest route is an environment variable for one run. `Program.cs` reads environment variables after user-secrets, so the variable wins and your secret stays as it was. From the repo root:

```bash
# macOS/Linux
AzureOpenAI__ReasoningEffort=Medium dotnet run --project src/FinanceAssistant
```

```powershell
# Windows (PowerShell)
$env:AzureOpenAI__ReasoningEffort = "Medium"
dotnet run --project src/FinanceAssistant
```

Ask something that rewards thinking:

```
> I earn 4200 EUR a month, rent is 1500, and I want 20000 saved in three years. Is that realistic?
```

Then run again without the override and ask the same question. On macOS/Linux that's the plain `dotnet run --project src/FinanceAssistant` from Step 4. In PowerShell the variable stays set for the whole session, so clear it first with `Remove-Item Env:AzureOpenAI__ReasoningEffort`.

> **Prefer to change the secret?** From `src/FinanceAssistant/`, `dotnet user-secrets set "AzureOpenAI:ReasoningEffort" "Medium"` does the same for every run until you change it again. Set it back afterwards (`None` on Path A, `Low` on Path B).

One thing moves reliably: the wait before the first word gets longer, because the model is spending tokens before it writes any. On `gpt-5.6-luna`, over three runs each, `None` answered in about 2.8 seconds and `Medium` in about 4.4 (time to the full reply, on our runs). Take `ConfigureOptions` out of the registration entirely and you land near 4.2, which is the medium default Step 2 warned you about. On Path B, `gpt-5-mini` gives you different numbers and a smaller gap, because `Low` still thinks a little. Time your own.

What often doesn't move is the answer. At this size both settings tend to come back with the same breakdown and the same numbers. The wording may differ even when the numbers match. That's the useful result rather than a disappointing one: in those runs on Luna you paid roughly 60% more latency for no visible gain, which is exactly why the dial is a per-feature decision instead of a global one. Ask something genuinely harder and the trade starts to pay.

If you still have the streaming code in place, run them together. If your deployment streams progressively, the reasoning pause becomes visible: at `None` or `Low` the text starts almost immediately, at `Medium` you watch nothing happen and then the answer arrives quickly once it starts. If your deployment buffers, you will see the same wait grow without the animation.

> **What the enum actually offers.** `ReasoningEffort` in `Microsoft.Extensions.AI` 10.9.0 is a five-value enum: `None`, `Low`, `Medium`, `High`, `ExtraHigh`. Neither workshop model lines up with it exactly. `gpt-5.6-luna` has a level above `ExtraHigh`, `max`. `gpt-5-mini` has one below `Low`, as Step 2 showed. Neither gap has an enum member. Reaching them means going around `IChatClient` to the provider's own options. That's the standing cost of a portable abstraction: it lags the newest knob on any one provider, and it can't promise every value works on every model. You accepted that trade the moment you typed `IChatClient`, and it's worth knowing you accepted it.

Before P2.01, make sure the fast setting is back. Clear the environment variable, or set the secret back to `None` or `Low`. Every exercise after this one is written and timed against the fast setting. If you kept the streaming code in place for this Extra, revert it now too (see "Then put it back").

On Path A that isn't only about speed. From P2.01 the agent sends tools with every request, and on the Chat Completions API, which this workshop uses, `gpt-5.6-luna` refuses tools at any reasoning effort above `None`:

```
Function tools with reasoning_effort are not supported for gpt-5.6-luna in /v1/chat/completions.
To use function tools, use /v1/responses or set reasoning_effort to 'none'.
```

The way out the message names, `/v1/responses`, is the Responses API, OpenAI's newer API for reasoning models. It takes tools at every effort level (tested November 2026). It isn't the workshop's default yet, because the rest of the workshop hasn't been tested on it. On Path B, `gpt-5-mini` with `Low` and tools works on Chat Completions. While the dial is up on Path A, the startup check prints a `[??]` warning about exactly this. It still lets the app start, because nothing sends tools yet.

---

## Troubleshooting

The startup check catches most setup problems before your code runs, and tells you the fix. If it says `[ok]` on every line and you still hit one of the errors below, look at your code first: `ServiceCollectionExtensions.cs`, then `Program.cs`. The entries below also cover runs with `StartupCheck__Skip=true`.

### `Missing AzureOpenAI:...` thrown at startup

Your secrets aren't being read. You want to confirm the four keys from P1.01 exist without printing their values into your terminal (handy if you're screen-sharing or pasting output into chat).

From `src/FinanceAssistant/`, run one of these.

On macOS/Linux (bash/zsh):

```bash
# Just the key names - values are masked.
dotnet user-secrets list | awk -F' = ' '{print $1}'

# Or: just count that all four keys are present.
dotnet user-secrets list | grep -c '^AzureOpenAI:'   # expect at least 4
```

On Windows (PowerShell):

```powershell
# Just the key names - values are masked.
dotnet user-secrets list | ForEach-Object { ($_ -split ' = ')[0] }

# Or: just count that all four keys are present.
(dotnet user-secrets list | Select-String '^AzureOpenAI:').Count   # expect at least 4
```

If you see fewer than four `AzureOpenAI:*` keys, you set them in the wrong folder (the user-secrets store is keyed off the project's `UserSecretsId`). Re-run the `dotnet user-secrets set` commands from `src/FinanceAssistant/`. More than four is fine: P2.02 adds `AzureOpenAI:EmbeddingDeployment` and P5.02 adds `AzureOpenAI:JudgeDeployment`.

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

If the deployment name checks out, look at the endpoint's path. `AzureOpenAI:Endpoint` must be the full v1 base URL, `https://your-resource.openai.azure.com/openai/v1/`. Store the bare resource root, `https://your-resource.openai.azure.com/`, and the SDK calls `https://your-resource.openai.azure.com/chat/completions`, which Azure answers with a 404 identical to the wrong-deployment-name case. The error message won't tell you which mistake you made, so check both. A missing trailing slash is not the cause: the code normalises it.

If the name and the path both check out, the endpoint points at the wrong resource. Copy it out of the portal again rather than retyping it.

### `UriFormatException` thrown at startup

The endpoint has no scheme. `your-resource.openai.azure.com/openai/v1/` without the leading `https://` isn't an absolute URI, so `new Uri(...)` refuses it before any request goes out. Add `https://` and re-set the secret.

### `400 Bad Request` naming `reasoning_effort`

The reasoning setting holds a level your model doesn't take. Check `AzureOpenAI:ReasoningEffort` against P1.01's table: `None` on Path A, `Low` on Path B. If you're on Path A and see this, check that the `chat` deployment really runs `gpt-5.6-luna`.

On Path B, this is what you get when the secret says `None`. `gpt-5-mini` rejects it. Set it to `Low`.

### `ArgumentException: Requested value '...' was not found` at startup

`Enum.Parse` couldn't read `AzureOpenAI:ReasoningEffort`. The secret holds a value the enum doesn't have, and the message quotes it. The valid ones are `None`, `Low`, `Medium`, `High` and `ExtraHigh`. `Minimal` and `Max` are real model levels, but they aren't enum members, so they can't be set from here.

### `AsIChatClient()` is not found

The `Microsoft.Extensions.AI.OpenAI` package isn't pulled in. Confirm `FinanceAssistant.csproj` references it, then run `dotnet clean` and `dotnet build` again.

### `OpenAIClient`, `ApiKeyCredential`, or `OpenAIClientOptions` is not found

`Microsoft.Extensions.AI.OpenAI` brings the OpenAI SDK in transitively, but the IDE may need `using OpenAI;` and `using System.ClientModel;` to see them. Both are at the top of `ServiceCollectionExtensions.cs` as written above.

---

## You can now

Type questions into the REPL and get real responses from your model: `gpt-5.6-luna` on Path A, `gpt-5-mini` on Path B.

The model has no tools yet, so factual questions about your finances will produce guesses. The system prompt that shapes the assistant's tone lives in `Prompts/SystemPrompt.md`. Edit that file, restart the REPL, and watch the assistant's personality shift. That's prompt-as-a-workflow in its smallest form.

---

## Summary

You've added:

- **Three packages**: `Microsoft.Extensions.AI`, `Microsoft.Extensions.AI.OpenAI`, and `Microsoft.Extensions.DependencyInjection`.
- **`ServiceCollectionExtensions.cs`**: an extension method that registers `IChatClient` against your Azure deployment via the OpenAI SDK's Azure v1 endpoint.
- **A client-wide default**: `ConfigureOptions` pins reasoning effort to the lowest value the enum offers for your model (`None` or `Low`), read from your secrets. Every call for the rest of the workshop inherits it, and Pillar 2 adds a second stage to the same pipeline.
- **Updated `Program.cs`**: three new `using`s, a `ServiceCollection`, the DI resolution, the `IChatClient` call inside the loop.
- **A working agent**: type a question, get a real reply.
- **Two Extras, if you took them**: a streamed reply, and a feel for what the reasoning dial costs and buys. Both reverted before P2.01.

---

## What's next

P2.01 is your first tool: a `ConvertCurrency` function with a hardcoded rate table. No DB, no embeddings. The point is to feel what `[Description]` quality means before the bigger transactions exercise lands later in the afternoon.

---

## Additional Resources

- [Microsoft.Extensions.AI documentation](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)
- [OpenAI .NET SDK on GitHub](https://github.com/openai/openai-dotnet)
- [Azure OpenAI v1 API surface](https://learn.microsoft.com/en-us/azure/foundry/openai/api-version-lifecycle)
