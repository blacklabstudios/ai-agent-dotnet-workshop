# P3.01 - The Agent Loop

> Pillar 3, Part 1. Individual.

## Mission

Unwrap the function-invocation middleware. Write the agent loop by hand in a `ChatAgent` class so you can see `ChatFinishReason`, manual tool invocation, and where the iteration cap goes.

By the end, the REPL behaves the same to the user but routes every turn through your own `ChatAgent`. The cap fires when a misbehaving model loops on tool calls, and you'll see exactly when and why.

**Learning Objectives**:

- Decide the loop by the model's tool calls, and read `ChatFinishReason` to tell why a turn stopped
- Invoke tools yourself with `AIFunction.InvokeAsync`
- Set an iteration cap to stop runaway tool calls
- See what `UseFunctionInvocation` was doing for you

---

## At a glance

**You'll leave with**

- The loop keeps going while the response carries `FunctionCallContent`, and the turn ends when there is none.
- `ChatFinishReason` only explains a stop, like `Length` (cut off) or `ContentFilter` (blocked).
- The catch: don't loop on `FinishReason` itself, because not every API fills it in.
- You invoke each tool with `AIFunction.InvokeAsync` and answer with a `FunctionResultContent` carrying its `CallId`.
- The iteration cap counts round-trips to the model, not tool calls, so a runaway loop stays bounded.

**The flow**

You · *a question* → **ChatAgent** · *your own loop* → Model · *asks for a tool* → Tool · *invoked by you* → Model · *no more calls* → Reply · *the final answer*

**Test it**

```text
$ dotnet run --project src/FinanceAssistant
> How much did I spend on Spotify in 2025, in JPY?
[agent] iteration 1: calling SearchTransactions
[agent] iteration 2: calling GetTransactions
[agent] iteration 3: calling Convert
[agent] iteration 4: final answer
```

Look for: `calling` lines that end in `final answer`, with `Convert` in a later iteration than the lookup. Which tools the model picks, their order and the number of iterations vary from run to run.

**Your agent so far**

- ✓ Has a model deployed and its keys stored locally
- ✓ Answers from a real model
- ✓ Calls a tool you wrote
- ✓ Reads your own data, by date and by meaning
- ✓ Runs its own loop, with a cap
- ✗ Remembers the conversation

## Prerequisites

- P2.02 finished. Three tools (Convert, Get, Search) work end-to-end through the function-invocation middleware.

---

## What we're solving

P2.01 added `UseFunctionInvocation()` to `ServiceCollectionExtensions.cs` so tool calls would run for you. That middleware owns this loop:

1. Send messages to the model.
2. If the model wants to call tools, invoke them.
3. Add the results to the message list.
4. Loop until the model stops asking for tools.

You never saw it. One `Use` call hid the loop.

In production code, `UseFunctionInvocation` is usually the right choice. Microsoft maintains it. It handles edge cases you haven't thought about. It's one line. We're not removing it because it's bad. We're removing it for this one exercise because the workshop is called "Build Your First AI Agent in .NET", and the loop is the agent. Two things land when you write it yourself:

1. **Visibility.** The loop is the agent. If you don't know it's there, you can't reason about how it fails. A model deciding to call the same tool five times in a row is a real failure mode. You only catch it if you can see the iteration count.
2. **A customisation seam.** Real systems eventually want to log every tool call, retry on transient failure, charge usage to a per-user budget, or swap the model mid-loop. The middleware is fine until any of those land. Once you've written the loop, you own it.

After this exercise, you have an informed choice: reach for the middleware most of the time, hand-write the loop when control matters. This is the one exercise that starts by removing code. One line comes out of `ServiceCollectionExtensions.cs`, then about 95 lines go into `ChatAgent.cs`.

---

## If you're comfortable, do this

Use this list if you want the route first. The full steps explain the choices and help you recover when a step fails.

1. Remove `.UseFunctionInvocation()` from `AddChatClient` in `ServiceCollectionExtensions.cs`. `ConfigureOptions` stays.
2. Create `src/FinanceAssistant/ChatAgent.cs`. Constructor takes `IChatClient`, `ChatOptions`, and a max-iterations integer (default 8). Public method `RunTurnAsync(IList<ChatMessage> messages, CancellationToken ct)` runs the loop.
3. Inside the loop: call `GetResponseAsync`, append the response messages to history, and collect its `FunctionCallContent` items. None means the turn is over: return the text, with a warning first if `FinishReason` is `Length` or `ContentFilter`. Otherwise invoke the matching `AIFunction` for each call, append a `FunctionResultContent` message, and continue. Hard cap at the configured max.
4. In `Program.cs`, instantiate `ChatAgent` once at startup and route the REPL turn through `chatAgent.RunTurnAsync(messages)` instead of `chatClient.GetResponseAsync(messages, chatOptions)`.
5. Run. Try the same prompts as P2.02 (single date, range, search, convert), plus `How much did I spend on Spotify in 2025, in JPY?`, which needs several tools in a row. Then deliberately force a runaway: edit `SystemPrompt.md` to instruct the model to call a tool repeatedly, construct the agent with `maxIterations: 3`, run again, watch the cap fire. Then revert both.

---

## Step 1: Remove UseFunctionInvocation

Open `src/FinanceAssistant/ServiceCollectionExtensions.cs`. Find the `AddChatClient` registration. Delete the one line that adds the function-invocation middleware:

```csharp
// REMOVE this single line from the chain
.UseFunctionInvocation()
```

Leave the rest of the pipeline where it is. `ConfigureOptions` stays, so reasoning effort is still pinned to `None` or `Low` and the loop you're about to write inherits it like every other call. The registration block becomes:

```csharp
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
```

Run the project and ask for something that needs a tool, like `Show me transactions on 2026-03-15`. After the startup check, you get a blank line and the prompt back:

```
Finance assistant. Type a message, or 'exit' to quit.
> 
> 
```

No error, no hang, no apology from the model. The model *did* answer: it asked the framework to invoke `GetTransactions`. That request lives in `response.Messages` as a `FunctionCallContent`, nothing invokes it, and `response.Text` is empty because the model hasn't written an answer yet. The REPL prints the empty string.

That blank line is the exact shape of the gap we're about to fill.

> The change feels like regression. It is. The middleware was good. We're removing it because this exercise is about understanding what it was doing. After P3.01, you'll have written the loop yourself and you'll know exactly why M.E.AI ships that middleware.

---

## Step 2: Create ChatAgent.cs

Create `src/FinanceAssistant/ChatAgent.cs`. The name is deliberate: [Demo 01](../demos/p6-01-migrate-to-agent-framework.md) swaps this class out for Microsoft Agent Framework's `ChatClientAgent`, which does the same job and is none of your code.

```csharp
using Microsoft.Extensions.AI;

namespace FinanceAssistant;

public class ChatAgent
{
    private readonly IChatClient _chatClient;
    private readonly ChatOptions _options;
    private readonly int _maxIterations;

    public ChatAgent(IChatClient chatClient, ChatOptions options, int maxIterations = 8)
    {
        _chatClient = chatClient;
        _options = options;
        _maxIterations = maxIterations;
    }

    public async Task<string> RunTurnAsync(IList<ChatMessage> messages, CancellationToken ct = default)
    {
        for (var iteration = 1; iteration <= _maxIterations; iteration++)
        {
            var response = await _chatClient.GetResponseAsync(messages, _options, ct);

            // The response carries the assistant's reply (which may include tool-call requests).
            // Append it to history so the tool-result messages we add below pair correctly with
            // the model's call requests on the next GetResponseAsync.
            foreach (var responseMessage in response.Messages)
            {
                messages.Add(responseMessage);
            }

            // Find every tool call in the response. Whether there are any decides what happens next.
            var toolCalls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .ToList();

            if (toolCalls.Count == 0)
            {
                // No tools asked for, so the turn ends here. The finish reason tells us how:
                // a finished answer, or one that was cut off (Length) or blocked (ContentFilter).
                if (response.FinishReason == ChatFinishReason.Length || response.FinishReason == ChatFinishReason.ContentFilter)
                {
                    Console.Error.WriteLine($"[agent] iteration {iteration}: stopped early, finish reason {response.FinishReason}");
                    return string.IsNullOrWhiteSpace(response.Text)
                        ? $"(no answer: the model stopped with {response.FinishReason})"
                        : response.Text;
                }

                Console.WriteLine($"[agent] iteration {iteration}: final answer");
                return response.Text;
            }

            // The model asked for tools. Invoke the matching AIFunction for each call.
            var toolNames = string.Join(", ", toolCalls.Select(c => c.Name));
            Console.WriteLine($"[agent] iteration {iteration}: calling {toolNames}");

            foreach (var call in toolCalls)
            {
                var function = _options.Tools?
                    .OfType<AIFunction>()
                    .FirstOrDefault(f => f.Name == call.Name);

                AIContent resultContent;
                if (function is null)
                {
                    resultContent = new FunctionResultContent(call.CallId, $"Tool '{call.Name}' is not registered.");
                }
                else
                {
                    try
                    {
                        var result = await function.InvokeAsync(new AIFunctionArguments(call.Arguments), ct);
                        resultContent = new FunctionResultContent(call.CallId, result);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Tool threw something the model didn't handle (or we didn't wrap at the tool boundary).
                        // Hand the model a structured error so it can recover or apologise.
                        resultContent = new FunctionResultContent(call.CallId, $"Tool error: {ex.Message}");
                    }
                }

                messages.Add(new ChatMessage(ChatRole.Tool, [resultContent]));
            }
        }

        // Hit the iteration cap. The model is probably stuck in a tool-call loop.
        // Return whatever the last assistant text was, or a fallback message.
        Console.Error.WriteLine($"[agent] iteration cap of {_maxIterations} hit. The agent looped on tool calls without producing a final answer.");
        var lastAssistantText = messages
            .Where(m => m.Role == ChatRole.Assistant)
            .Select(m => m.Text)
            .LastOrDefault(t => !string.IsNullOrWhiteSpace(t));
        return lastAssistantText ?? "(no final answer, iteration cap reached)";
    }
}
```

Six things worth reading carefully:

**Tool calls drive the loop. The finish reason explains a stop.** Every response carries a `FinishReason` saying why the model stopped writing: `Stop` (finished), `ToolCalls` (wants tools run), `Length` (ran out of output tokens mid-answer) or `ContentFilter` (the service's filter blocked it). It's tempting to loop on it directly. Don't, because the evidence that the model wants tools is the `FunctionCallContent` itself, and not every API fills in the reason. OpenAI's newer Responses API leaves `FinishReason` empty on a completed answer, tool calls or not (tested November 2026). This project uses Chat Completions, which does fill it in, so a breakpoint here will show `ToolCalls` on every tool turn. Other `IChatClient`s may not. That's also why `UseFunctionInvocation` looks for `FunctionCallContent` and never reads the reason.

So the loop asks one question first: are there tool calls? If yes, run them. If no, the turn ends, and only then does the finish reason matter, to tell a finished answer from a cut-off or blocked one. Skip that check and a cut-off answer comes back as if it were complete, with no sign anything went wrong. You'll rarely see `Length` or `ContentFilter` here, since nothing in this workshop sets a token limit. They're the cases that turn up in production first.

**The `for` loop bound.** This is the iteration cap. Eight is a reasonable default. The cap exists because a model can decide to call a tool, see the result, decide to call it again, see the same result, and never produce a final answer. Without the cap, the loop would run until you Ctrl+C or your token budget runs dry. With the cap, the worst case is bounded. One iteration is one round-trip to the model, not one tool call. The model can emit several `FunctionCallContent` items in a single response and our loop counts them all as one iteration. Eight round-trips is plenty for legitimate workflows. The Step 5 runaway prompt has to work hard to force one call per turn, precisely because parallel calls would converge in two or three iterations and never trip the cap.

The cap is checked when the loop comes back round, not before the tools run. On the last iteration the loop still invokes every tool the model asked for and appends the results, then exits, so the model never sees those results. That's harmless for read-only tools. Once P5.01 adds the `Transfer` tool, though, a gated action could run on that final lap without the model ever hearing about it. If that bothers you, a variant is to check `iteration == _maxIterations` before invoking tools and stop there instead. The listing above leaves it as is.

**`response.Messages` vs `response.Text`.** When the model returns a non-tool answer, `response.Text` is the convenient string. When the model wants to call tools, the actual structure lives in `response.Messages`, which contains a `ChatMessage` with `FunctionCallContent` items. We append those messages to history so the model can see what it asked for, then we invoke each tool and append the result.

**`FunctionResultContent` carries the `CallId`.** The model issued each tool call with a unique ID. The result message has to reference that ID so the model knows which call this result is for. Get the ID wrong and the model loses its place in the conversation.

**`new AIFunctionArguments(call.Arguments)` wraps the model's arguments.** `call.Arguments` is an `IDictionary<string, object?>?` holding the named arguments the model produced, and `AIFunctionArguments` accepts it directly, null included. No copying, no defaulting. The `catch` around `InvokeAsync` lets `OperationCanceledException` through on purpose. A cancelled turn should stop, not become a tool error the model tries to recover from.

**The `[agent]` lines.** Two `Console.WriteLine` calls turn the loop from invisible plumbing into something you can watch. One when the model asks for tools (names them), one when the model produces a final answer. Both carry the iteration number. The other two, the early stop and the cap after the loop, are `Console.Error.WriteLine`, because they're warnings rather than chatter. At a terminal all four interleave and you won't notice the difference. Pipe stdout somewhere and the warnings stay on stderr. Workshop chatter on purpose. In a real system, swap all four for an `ILogger<ChatAgent>` so the output goes through your logging pipeline instead of the console.

---

## Step 3: Wire ChatAgent into Program.cs

Two edits to `Program.cs`. No new `using` directives needed: `ChatAgent` lives in the `FinanceAssistant` namespace, already in scope.

### 3.1: Instantiate ChatAgent after the tools are registered

Find the existing `chatOptions` block:

```csharp
var chatOptions = new ChatOptions
{
    Tools =
    [
        AIFunctionFactory.Create(convertCurrency.Convert),
        AIFunctionFactory.Create(getTransactions.GetTransactions),
        AIFunctionFactory.Create(searchTransactions.SearchTransactions)
    ]
};
```

Right after that block, add a single line that constructs the `ChatAgent`:

```csharp
var chatAgent = new ChatAgent(chatClient, chatOptions);
```

### 3.2: Route the turn through ChatAgent

Find these two lines in the REPL `while` loop:

```csharp
var response = await chatClient.GetResponseAsync(messages, chatOptions);
Console.WriteLine(response.Text);
```

Replace them with:

```csharp
var reply = await chatAgent.RunTurnAsync(messages);
Console.WriteLine(reply);
```

The REPL no longer talks to `chatClient` directly. Every user turn goes through `ChatAgent`, which owns the agent loop end to end.

One thing to notice: `RunTurnAsync` appends to the list you hand it. Every assistant reply and tool result the loop produces lands in `messages`. But `Program.cs` rebuilds `messages` from scratch at the top of each REPL iteration, so all of that is thrown away the moment the turn ends. That's why the agent still can't answer "what was the total again?". The loop has the history. The REPL discards it. P4.01 closes exactly that gap.

---

## Step 4: Run and verify the same prompts still work

From the repo root:

```bash
dotnet run --project src/FinanceAssistant
```

Try the four prompts from P2.02, plus a fifth that needs several tools in a row. Each should behave the same as before, with one new visible difference: the `[agent]` lines now print on every turn, showing exactly which tools fire and when the model produces its final answer.

- `Show me transactions on 2026-03-15` (Get tool, single ISO date)
- `Show me transactions between 2026-01-01 and 2026-01-31` (Get tool, range syntax)
- `Find anything about coffee` (Search tool)
- `Convert 100 EUR to USD` (Convert tool)
- `How much did I spend on Spotify in 2025, in JPY?` (two or more tools, in sequence, in one turn)

For the simple one-shot prompts, you'll see two `[agent]` lines: one tool call, then a final answer. The last prompt is the one that needs the loop: the model can't convert an amount it hasn't looked up yet, so the lookup and the conversion can't happen in the same step. A run looks something like this:

```
[agent] iteration 1: calling SearchTransactions
[agent] iteration 2: calling GetTransactions
[agent] iteration 3: calling Convert
[agent] iteration 4: final answer
```

The tools the model picks, and their order, may vary from run to run. What stays the same is that the conversion comes after the lookup, in a later iteration.

This is the "find, then total" sequence P2.02 pointed at. The loop lets the agent search and then fetch in one turn. It doesn't make the total real. A total over `SearchTransactions` results is still a total of the top K matches, not of every matching row, and the adding up still happens in the model. A real total needs a tool that does the sum in code, like the `SumTransactions` tool [Demo 02](../demos/p6-02-multi-agent-handoff.md) adds.

Same outputs, different code path, more visibility. The agent loop is now under your control without changing the user-facing behaviour.

---

## Step 5: Force the runaway and watch the cap fire

You want to see the iteration cap actually doing its job. The cleanest way is to give the model a system prompt that instructs it to over-call a tool, and lower the cap for this one run.

Open `src/FinanceAssistant/Prompts/SystemPrompt.md` and append a new paragraph at the end:

```
For every user question, you MUST call the SearchTransactions tool repeatedly, ONE call per turn (never in parallel), with a different query each time. After seeing each result, immediately call SearchTransactions again with a new query. Do not produce a final answer; just keep searching forever.
```

Then, in `Program.cs`, change the line you added in Step 3.1 to:

```csharp
var chatAgent = new ChatAgent(chatClient, chatOptions, maxIterations: 3);
```

Why 3 and not the default 8? The prompt asks. The model decides. Even with every clause in place, a model can decide it has searched enough and answer anyway. In one run with the default of 8, `gpt-5.6-luna` searched five times and answered at iteration 6. With a cap of 3, the same prompt and question hit the cap. Those runs were on Path A. Path B's `gpt-5-mini` may behave differently. The lower cap keeps the step about the cap, not about winning an argument with the model.

Every clause in that paragraph is load-bearing, and the way to see it is to paraphrase one and watch the cap stop firing.

"ONE call per turn (never in parallel)" exists because a model will happily emit three or more tool calls in a single response, and our loop counts that whole response as *one* iteration. Batched calls burn through the work in two or three iterations and never trip the cap.

"Do not produce a final answer", together with "immediately call SearchTransactions again", exists because the model gives up otherwise. Drop them and it keeps calling the tool one per turn, exactly as instructed, then quietly answers anyway. If it does that before the cap, there's nothing to see.

Both failure modes look the same from outside: the run ends with a final answer instead of the cap line. If that happens to you, put the clause back rather than raising the cap. Troubleshooting has more.

Stop the REPL if it's still running, save both files, and run again. Ask a real question. Try something like `How much did I spend on coffee?`. `Hello` is too trivial. The model will ignore the override if the question doesn't justify any tool call. Watch the `[agent]` lines fire with `SearchTransactions` every time. If the model plays along, after three iterations you'll see:

```
[agent] iteration 1: calling SearchTransactions
[agent] iteration 2: calling SearchTransactions
[agent] iteration 3: calling SearchTransactions
[agent] iteration cap of 3 hit. The agent looped on tool calls without producing a final answer.
(no final answer, iteration cap reached)
```

That last line is the fallback, and in a runaway like this one it's what you'll almost always get, unless the model narrated alongside one of its calls. The `LastOrDefault` above it looks for the most recent assistant message with text in it, but every assistant message in a tool-call loop carries `FunctionCallContent` and no text of its own, so there's nothing for it to find. The lookup earns its place for the messier real cases, where a model narrates ("Let me check that for you...") in the same message it requests a tool. Here it comes up empty and the fallback speaks.

Either way, the turn ended. The cap protected your token budget.

Once you've seen it, revert both edits. Remove the paragraph from `SystemPrompt.md`, set the `Program.cs` line back to `new ChatAgent(chatClient, chatOptions)`, and restart so the agent goes back to behaving normally.

> This is a deliberately silly prompt. Real runaway loops come from subtler causes: a tool whose result keeps confusing the model, a typo in a tool description that makes the model retry endlessly, an injected user input that says "keep searching until you find X". Whatever the cause, the cap is the thing standing between your bug and your bill.

---

## Troubleshooting

### A prompt that needs a tool prints a blank line and nothing else

This is the Step 1 state. The model asked for a tool call, nothing invoked it, and `response.Text` was empty. Either you removed `UseFunctionInvocation` but haven't wired `ChatAgent` yet, or `Program.cs` is still calling `chatClient.GetResponseAsync` directly. Confirm Step 3.2. The REPL turn has to go through `chatAgent.RunTurnAsync`.

### `FunctionCallContent` or `FunctionResultContent` is not found

These types live in `Microsoft.Extensions.AI`. The using is at the top of `ChatAgent.cs` as written.

### Tool calls work for one round but the agent says "I don't know" in the same turn

You're appending the response messages but not the tool-result messages, or vice versa. Read the loop again. Both have to land in the `messages` list before the next iteration's `GetResponseAsync` call.

If it's the next REPL turn that forgets what you asked before, that's expected. The end of Step 3 explains why, and P4.01 fixes it.

### The model answered before the cap fired

The runaway prompt asks. The model decides. Even with every clause in place, it can decide it has searched enough and write an answer. Check three things. The paragraph is in `SystemPrompt.md` word for word. Your question needs a search, so not `Hello`. `Program.cs` constructs the agent with `maxIterations: 3`, not the default 8. If all three hold and it still answers first, run it once more. Don't make the prompt louder. The step is about the cap, not the prompt.

### Cap fires on normal prompts

First check you reverted both Step 5 edits. The runaway paragraph should be gone from `SystemPrompt.md`, and `Program.cs` should read `new ChatAgent(chatClient, chatOptions)` with no third argument.

If both are back to normal, the model is asking for tool calls every turn and your loop is treating one tool-calling response as one iteration. That's correct. If the cap of 8 is too tight for legitimate workflows in your domain, raise it. If it's firing in normal use, the system prompt or tool descriptions are pushing the model into excessive tool use.

---

## You can now

See the agent loop. Every tool call, every iteration, every decision to stop or go on is a line of code in front of you. The cap is a `for` loop bound, not a config knob.

Edit it. Add logging at the top of each iteration to print the iteration number and the model's last text. Add per-tool retry logic. Cache identical tool calls. The loop is yours now.

---

## Summary

You've changed:

- **Removed** `.UseFunctionInvocation()` from `AddChatClient`. The chat client is back to what P1.02 registered: reasoning effort pinned, no tool middleware.
- **`ChatAgent.cs`**: a hand-written agent loop. Calls the model, appends response messages to history, invokes tools by matching `FunctionCallContent.Name` against the registered `AIFunction`, appends `FunctionResultContent` results, and repeats until the model stops asking for tools or the iteration cap fires. Only when a response has no tool calls does it read `ChatFinishReason`, and it warns when that says the last answer was cut off (`Length`) or blocked (`ContentFilter`).
- **`Program.cs` updated**: the REPL now routes every user turn through `chatAgent.RunTurnAsync` instead of calling the chat client directly.
- **A working iteration cap**: forced a runaway with a cap of 3, watched the cap fire, restored the prompt and the default cap.

---

## What's next

P4.01 starts Day 2 with conversation memory. Right now every REPL turn is independent: the agent forgets what you asked on the previous turn. We'll wrap the message list in a `ConversationStore` that persists across turns inside a session, so the agent can finally answer "what was the total again?" correctly.

---

## Additional Resources

- [Microsoft.Extensions.AI overview](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)
- [Anthropic: building effective agents (the agent-loop pattern)](https://www.anthropic.com/engineering/building-effective-agents)
