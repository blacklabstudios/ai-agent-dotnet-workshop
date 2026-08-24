# P2.01 - Add a ConvertCurrency Tool

> Pillar 2, Part 1. Individual.

## Mission

Add a `ConvertCurrency` tool to the agent. The tool is a hardcoded-rate currency converter the model can call when the user asks something like "Convert 100 EUR to USD". You'll wire it through M.E.AI's function-invocation pipeline so the agent calls it automatically.

By the end, the REPL answers conversion questions with real numbers from your tool, not training-data guesses.

**Learning Objectives**:

- Describe a tool with `[Description]` attributes
- Wrap a method as an AI tool with `AIFunctionFactory.Create`
- Use the function-invocation pipeline (`UseFunctionInvocation`)
- Write descriptions the model can use to select and call a tool
- Recognise the same pattern behind provider-side web search

---

## Prerequisites

- P1.02 finished. The REPL replies to `hello` with a sentence from your `gpt-5.6-luna` deployment.

---

## What we're solving

Ask the agent "Convert 100 EUR to USD" now and it guesses. Its training data does not give you a current rate or a source for one.

Give the agent a function it can call: `Convert(amount, fromCurrency, toCurrency)`. It uses a fixed rate table. The model chooses when to call it and supplies the arguments. M.E.AI sends the result back into the conversation for the model to answer from.

> **The rate table is static on purpose.** In a real system this tool would take an `IRatesService` in its constructor and call out to a live rates API, with caching and error handling. The tool plumbing is identical either way: same class shape, same `[Description]` attributes, same registration. The difference is only what's behind the method body. P2.02 demonstrates the service-injected variant when we add transaction tools backed by `ITransactionsService`. For now, hardcoded keeps the exercise focused on the wiring and the descriptions, not on the rates.

The most important thing in this exercise is not the C# code. It's the `[Description]` attributes.

The model never sees your method body. It sees:

1. The method name.
2. The method's `[Description]` text.
3. Each parameter's name, type, and `[Description]` text.

That metadata is the entire API the agent reasons over. Vague descriptions mean wrong tool calls. Specific descriptions mean the agent picks the right tool with the right arguments. Anthropic calls this the agent-computer interface (ACI).

How much that matters scales with the tool. On a three-parameter converter in an obvious domain, a current model papers over a weak description using the method name, the parameter names and the types. The Extra at the end of this guide has you try to make it fail, and find out how hard that is. On a fifteen-parameter tool there's nothing left to paper over with, and the description is the whole interface.

---

## If you're comfortable, do this

Use this list if you want the route first. The full steps explain the choices and help you recover when a step fails.

1. Create `src/FinanceAssistant/Tools/ConvertCurrencyTool.cs`. One class, one method, hardcoded rate table, `[Description]` on the method and on every parameter.
2. Update `ServiceCollectionExtensions.cs` to add `UseFunctionInvocation()` to the chat-client pipeline so M.E.AI auto-invokes tool calls.
3. In `Program.cs`, instantiate the tool, wrap its method with `AIFunctionFactory.Create`, and put it into a `ChatOptions.Tools` list.
4. Pass the `ChatOptions` to `GetResponseAsync` inside the loop.
5. Run. Type "Convert 100 EUR to USD". Confirm a real number from the rate table appears.

Read "Where you have already seen this" after Step 4 even if you skip everything else. It's the section that connects what you just built to the web search button in every chat product you've used. The Extra after it is optional.

---

## Step 1: Create ConvertCurrencyTool.cs

`Tools/` doesn't exist yet. Create the folder, then create `src/FinanceAssistant/Tools/ConvertCurrencyTool.cs` inside it:

```csharp
using System.ComponentModel;

namespace FinanceAssistant.Tools;

public class ConvertCurrencyTool
{
    // Rates expressed in USD per unit of the source currency.
    // Hardcoded for the workshop. In a real system this would come from a live rates API,
    // typically injected as an IRatesService in the constructor.
    private static readonly Dictionary<string, decimal> RatesToUsd = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = 1.00m,
        ["EUR"] = 1.10m,
        ["GBP"] = 1.27m,
        ["JPY"] = 0.0067m,
        ["CHF"] = 1.13m,
        ["CAD"] = 0.74m,
        ["AUD"] = 0.66m,
    };

    [Description("Convert an amount from one currency to another using fixed reference rates. Returns a string like '100 EUR = 110.00 USD'. Supports USD, EUR, GBP, JPY, CHF, CAD, AUD.")]
    public string Convert(
        [Description("The amount to convert, in the source currency. A positive number.")] decimal amount,
        [Description("The 3-letter ISO currency code of the source amount, e.g. EUR, USD, GBP.")] string fromCurrency,
        [Description("The 3-letter ISO currency code of the target currency, e.g. USD, EUR, JPY.")] string toCurrency)
    {
        if (!RatesToUsd.TryGetValue(fromCurrency, out var fromRate))
            return $"Unknown source currency '{fromCurrency}'. Supported: {string.Join(", ", RatesToUsd.Keys)}.";

        if (!RatesToUsd.TryGetValue(toCurrency, out var toRate))
            return $"Unknown target currency '{toCurrency}'. Supported: {string.Join(", ", RatesToUsd.Keys)}.";

        var amountInUsd = amount * fromRate;
        var converted = amountInUsd / toRate;
        return $"{amount} {fromCurrency} = {converted:F2} {toCurrency}";
    }
}
```

> Two things worth noticing about the `[Description]` text.
>
> First, the method-level description tells the agent what the tool returns and in what shape. The agent uses that to decide whether the tool's output is what it needs.
>
> Second, the parameter descriptions name the format ("3-letter ISO currency code") and give examples ("EUR, USD, GBP"). Without those examples the agent might pass "euros" and your dictionary lookup falls through to the unknown-currency branch. With them the agent maps "euros" to "EUR" before it calls.

> **A note on the method name.** `Convert` shadows `System.Convert` inside this class. That's fine here because we never call `System.Convert` from inside the tool. If you later add something like `Convert.ToDecimal(...)` in the method body, you'll need to fully qualify it as `System.Convert.ToDecimal(...)`.

---

## Step 2: Add UseFunctionInvocation to the chat client

Open `src/FinanceAssistant/ServiceCollectionExtensions.cs`. The `AddChatClient` method already builds its client through a pipeline. P1.02 put one stage in there, `ConfigureOptions`, to pin reasoning effort. We're adding a second stage to the same pipeline: the function-invocation middleware.

Find the `return services.AddSingleton<IChatClient>(_ => ...)` block. Add `.UseFunctionInvocation()` after the `ConfigureOptions` call and before `.Build()`. The block becomes:

```csharp
return services.AddSingleton<IChatClient>(_ =>
    new OpenAIClient(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = apiBase })
        .GetChatClient(deployment)
        .AsIChatClient()
        .AsBuilder()
        .ConfigureOptions(o =>
            o.Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None })
        .UseFunctionInvocation()
        .Build());
```

One new line. It wraps the chat client with middleware that:

1. Receives the model's response.
2. If the response includes tool-call requests, M.E.AI invokes the matching tool method with the model's arguments.
3. The tool result is added to the message list, and the chat is re-issued automatically.
4. The loop continues until the model returns a non-tool response.

That's the agent loop, hidden behind one `Use` call. Pillar 3 unpacks what's inside. For now, this is enough to make tools work.

---

## Step 3: Register the tool in Program.cs

Three small changes.

### 3.1: Add a using

At the top of `Program.cs`, alongside the existing `using` lines, add:

```csharp
using FinanceAssistant.Tools;
```

### 3.2: Build the tool list once at startup

Right after the `var chatClient = provider.GetRequiredService<IChatClient>();` line, add:

```csharp
var convertCurrency = new ConvertCurrencyTool();
var chatOptions = new ChatOptions
{
    Tools = [AIFunctionFactory.Create(convertCurrency.Convert)]
};
```

`AIFunctionFactory.Create(method)` reflects over the method, picks up the `[Description]` attributes, generates the JSON schema for the parameters, and returns an `AIFunction` that M.E.AI's function-invocation middleware can invoke.

### 3.3: Pass the options to GetResponseAsync

Find this line in the loop:

```csharp
var response = await chatClient.GetResponseAsync(messages);
```

Replace it with:

```csharp
var response = await chatClient.GetResponseAsync(messages, chatOptions);
```

---

## Step 4: Run

From the repo root:

```bash
dotnet run --project src/FinanceAssistant
```

Try a few prompts in turn:

- `Convert 100 EUR to USD`
- `How much is 50 GBP in JPY?`
- `What's 10 dollars in euros?`

Each should print a sentence containing a number from your rate table.

The first prompt is the easy one. The second tests whether the agent normalises the currency names you wrote into ISO codes. The third tests whether it maps "dollars" to "USD" and "euros" to "EUR" before it calls.

The third one is the interesting one. If it comes back with `USD` and `EUR`, something in the tool's metadata told the model what shape those parameters wanted. The Extra works out how much of that was your description text and how much was everything else.

> **Confirming the tool actually ran.** The failure mode where the model bluffs an answer without calling the tool looks identical to success. Both print a number. To confirm the tool is being invoked, drop a `Console.WriteLine($"[tool] Convert({amount}, {fromCurrency}, {toCurrency})");` at the top of `Convert`, or set a breakpoint there. You should see the tool fire on every conversion prompt.
>
> Keep the line in while you work through the Extra below, because it's the only way to see what arguments the model chose. Take it out afterwards. `p2-01-end` doesn't have it.

---

## Where you have already seen this

You just built the mechanism behind provider-side web search.

Turn on web search in any chat API and nothing magic happens underneath. The provider registers a tool, roughly `web_search(query)`, with a description written by their team. The model decides a question needs information it doesn't have, emits a call to that tool with a query string it wrote itself, something runs the search, and the results come back into the conversation as a tool result. The model answers from them.

Four beats. The same four you just watched fire on `Convert 100 EUR to USD`.

| Your tool | Provider web search |
| --- | --- |
| `Convert(amount, from, to)` | `web_search(query)` |
| the `[Description]` you wrote | a description their team wrote |
| your rate table | their search index |
| `UseFunctionInvocation()` runs it in your process | their infrastructure runs it in theirs |

The only real difference is who hosts the tool, and that difference is bigger than it sounds. A tool running in your process is one you can breakpoint, cache, rate limit, log, and point at your own data. A hosted one is none of those. You send a question and trust what comes back.

That's a fine trade for general web search, where you were never going to run a crawler anyway. It's a bad trade for your transactions, which is why P2.02 is a tool you own end to end.

The corollary is the useful part. There's no category of "AI that can browse the web" separate from what you're doing here. There's a model, a tool description, and something that runs the tool. Once you've seen it from the inside, the rest of the product category stops being mysterious.

---

## Extra: try to break the descriptions

This guide claims the `[Description]` text is the API. Ten minutes to find out how much weight that claim actually carries on a current model.

In `ConvertCurrencyTool.cs`, strip the three parameter descriptions down to nothing useful. Leave the method description exactly as it is for now:

```csharp
[Description("Convert an amount from one currency to another using fixed reference rates. Returns a string like '100 EUR = 110.00 USD'. Supports USD, EUR, GBP, JPY, CHF, CAD, AUD.")]
public string Convert(
    [Description("The amount.")] decimal amount,
    [Description("The source currency.")] string fromCurrency,
    [Description("The target currency.")] string toCurrency)
```

Run, and ask the prompt that needs the mapping:

```
> What's 10 dollars in euros?
```

What you're hoping for is `Unknown source currency 'dollars'` coming back from your own code. That would be the model passing a word your dictionary has never heard of, because nothing told it the parameter wanted `USD`.

You almost certainly won't get it. That's the exercise.

### Keep taking signal away

Each rung removes more. Run `What's 10 dollars in euros?` again after each one and watch the `[tool]` line to see what the model actually chose.

1. **Rename the parameters to `a`, `b` and `c`.** Use your IDE's rename refactor rather than editing the signature by hand. The body reads the three parameters eight times between the two lookups, the two error strings and the final return, and with `TreatWarningsAsErrors` on you get a wall of CS0103 if you miss any. Now the names carry nothing either.
2. **Ask in a language where the answer doesn't resemble the code.** `Quanto são 10 dólares em euros?`
3. **Gut the method description too.** Cut it to `[Description("Convert an amount from one currency to another.")]`. That removes the ISO list and the `100 EUR = 110.00 USD` example, which were the two strongest hints left on the tool.

By rung three the model has a method called `Convert`, three parameters called `a`, `b` and `c`, two of them strings, and the word "currency". On `gpt-5.6-luna` the tool still gets called as `Convert(10, USD, EUR)`. Every rung, including the last.

If yours breaks at any rung, say so in the room. That's the more interesting result, and it's worth comparing.

### What to take from that

The obvious reading is that the descriptions don't matter. That's the wrong one.

Your `[Description]` text was never the only signal. The method name, the parameter names, the parameter types and the method description all carry meaning, and a strong signal covers for a weak one. This tool has three parameters, an unmistakable name, and a domain the model has seen a million times. There's so much redundancy that removing any one thing changes nothing.

That redundancy is exactly what runs out. On a tool with fifteen parameters, half of them strings, half of them ambiguous, "everything else is obvious" stops being true, and the description is all that's left. You'll write one of those long before you write another `Convert`.

So the honest version of the lesson isn't "vague descriptions break your agent". It's this: you can't prove the descriptions matter by breaking a three-parameter currency converter, and the reason you can't is the same reason they'll matter on the next tool you build.

Put the method description, the three parameter descriptions and the real parameter names back before P2.02, and take out the `[tool]` line if you added one. `p2-01-end` has the real text and no debug line. Three tools compete for every question in the next exercise, and this is the text that decides which one wins.

---

## Troubleshooting

### The answer looks right but the number is wrong

You ask `Convert 100 EUR to USD` and get something like:

```
100 EUR ≈ 108 USD.

Exchange rates fluctuate; this uses an approximate rate of 1 EUR = 1.08 USD.
```

Nothing threw. Nothing apologised. That number came out of training data, not out of your rate table, which gives exactly `110.00`. The polite hedge about fluctuating rates is the tell: your tool doesn't hedge, it returns one string.

The tool never reached the model. Check Step 3.3. The call has to be `GetResponseAsync(messages, chatOptions)`, not `GetResponseAsync(messages)`. Without the options argument the model is handed no tools at all, so it answers from what it remembers.

This is the failure mode the Step 4 callout warns about, and it's the one worth remembering, because it doesn't look like a failure. Add the `[tool]` line and it becomes obvious in one run.

### The agent prints a blank line

You type a conversion prompt, get an empty line, and land back at the `>` prompt. No exception, no message.

`UseFunctionInvocation()` is missing from Step 2. The model did request the tool call, and the response carries that request as `FunctionCallContent`, but nothing invoked it and nothing produced any text. `response.Text` is empty, so `Console.WriteLine` prints an empty line.

A blank assistant line is a signal, not noise. It means the response came back carrying something other than text. P4.02 has you read one again for a different reason.

### Agent calls the tool with a currency name instead of a code

You see `Unknown source currency 'dollars'` come back from your own code. Look at the `[Description]` on `fromCurrency` and `toCurrency`. If they don't say "3-letter ISO currency code" and give examples, the model has less to work with and may pass "euros" or "USD dollars", and your dictionary lookup falls through to the unknown-currency branch.

If you can't reproduce this even with the descriptions stripped, that's the expected result on a current model. The Extra covers why, and why it doesn't let the descriptions off the hook.

### `AIFunctionFactory` is not found

It lives in `Microsoft.Extensions.AI`. Confirm `using Microsoft.Extensions.AI;` is at the top of `Program.cs`. (It already is, from P1.02.)

### `UseFunctionInvocation` is not found

Same package as `AIFunctionFactory`. Two things to check:

1. `using Microsoft.Extensions.AI;` is present in `ServiceCollectionExtensions.cs`. Both `AsBuilder()` and `UseFunctionInvocation()` are extension methods that won't resolve without it. (It's already there from P1.02. If you refactored usings, this is where it goes missing.)
2. Your `Microsoft.Extensions.AI` reference is `10.9.0`, the version P1.02 added. The function-invocation middleware has shipped since `10.5.2`, so anything from that version up resolves `UseFunctionInvocation()`.

---

## You can now

Type natural-language conversion questions and get real numbers out of your hardcoded rate table. The agent normalises "dollars" or "euros" into ISO codes before calling the tool, because the tool's metadata told it what those parameters wanted. That metadata is the whole API the agent reasons over, and `[Description]` is the part of it you write on purpose rather than by accident.

---

## Summary

You've added:

- **`Tools/ConvertCurrencyTool.cs`**: a hardcoded-rate currency converter with `[Description]` on the method and on every parameter.
- **`UseFunctionInvocation()`**: the M.E.AI middleware that auto-invokes tool calls inside the chat client.
- **`ChatOptions.Tools`**: the list of tools the agent can choose from on each turn.
- **A working tool call**: ask the agent to convert currencies, watch it call your method.
- **A read on provider-side web search**: same four beats, hosted somewhere you can't breakpoint.

You've also seen the two ways this goes wrong, and neither one throws: a confident wrong number when the tool list never reached the model, and a blank line when nothing was there to invoke the call.

---

## What's next

P2.02 adds two real tools backed by your transactions database: `GetTransactions` (date-range query) and `SearchTransactions` (semantic search). The bigger lesson there lands at the failure boundary, when a parser throws on natural-language input and the agent has to recover.

---

## Additional Resources

- [Microsoft.Extensions.AI tool calling](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)
- [Anthropic on agent-computer interfaces](https://www.anthropic.com/engineering/building-effective-agents)
