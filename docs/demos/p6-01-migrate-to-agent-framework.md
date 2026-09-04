# Demo 01 - Migrate the Finance Assistant to Microsoft Agent Framework

> Demo 1. Facilitator drives, room watches, nobody types.

## Mission

Replace the hand-written loop, tool dispatch, and message list with Microsoft Agent Framework. Delete `ChatAgent.cs`, `ConversationStore.cs`, and `SummarizingHistoryReducer.cs`. The REPL keeps the same behaviour as `p6-01-end` for every prompt except `TransferFunds`. The four tools, system prompt, and Azure OpenAI wiring stay put.

**Learning Objectives**:

- Replace `ChatAgent` with `Microsoft.Agents.AI.ChatClientAgent`
- Replace `ConversationStore` with a framework-owned `AgentSession`
- See where M.E.AI ends and MAF starts

---

## Prerequisites

- The repo is on the `p6-01-end` branch. Run `git status` and confirm the tree is clean.
- Run `dotnet build FinanceAssistant.sln`. It has to succeed with zero warnings.
- Azure OpenAI user-secrets for `finance-assistant-workshop` are still wired (`AzureOpenAI:Endpoint`, `AzureOpenAI:ApiKey`, `AzureOpenAI:Deployment`, `AzureOpenAI:EmbeddingDeployment`).
- The pgvector Postgres container is running. Check it with `docker compose ps` and confirm the STATUS column reads `Up`. The compose file defines no healthcheck, so do not wait for the word `healthy`.

> `FinanceAssistant.sln` holds two projects: `src/FinanceAssistant` and `src/FinanceAssistant.McpServer`. It doesn't hold `tests/FinanceAssistant.Evals`. A green solution build isn't proof the whole repo compiles. This matters in Step 2.

---

## What we're solving

The hand-written `ChatAgent` we shipped in Pillar 3 is honest about what an agent is. There's a `for` loop, a `GetResponseAsync` call inside it, a check for `FinishReason.ToolCalls`, a loop over `FunctionCallContent` items, an `InvokeAsync` per tool, and a `FunctionResultContent` appended to the message list before the next turn. We wrote it on purpose so the loop, the tool-call boundary, and the message bookkeeping all stay in sight.

That works for teaching and for one agent in one process. It gets difficult when you add another agent, keep a conversation across a restart, pause an approval for hours, or hand work to a specialist. Those cases need a loop that other code can compose.

Microsoft Agent Framework is that abstraction. The loop becomes `ChatClientAgent`. The message list becomes `AgentSession`. The tools themselves are untouched (still `AIFunction`, still `AIFunctionFactory.Create`, still `[Description]`-driven schemas), because MAF leans on M.E.AI for that. What changes is who calls them. What MAF adds is an agent type you can pass to a workflow, a session type the framework can serialise, and the orchestration primitives we'll use in Demo 2 (handoff, group chat, graph).

We're going to do this in three pieces:

1. **Add the MAF package.** One `PackageReference`. The existing M.E.AI packages stay, because MAF builds on them.
2. **Replace the loop.** Construct a `ChatClientAgent` from the same `IChatClient` we already register, hand it the same four tools, point it at the same system prompt. Delete `ChatAgent.cs`, `ConversationStore.cs` and `SummarizingHistoryReducer.cs`.
3. **Use it from `Program.cs`.** `await agent.CreateSessionAsync()` once at startup, `agent.RunAsync(input, session)` inside the REPL loop. That's the whole REPL.

Two things leave the build on the way through, and both are choices rather than limitations.

> We're dropping the `ApprovalRequiredAIFunction` confirmation gate from `TransferFundsTool`, to keep the diff to one idea. Approvals are a first-class agent-level concern in MAF and Pillar 5's work carries over. See "Transfer prompts no longer ask for confirmation" in Troubleshooting.

> We're also breaking `tests/FinanceAssistant.Evals`, which calls `ChatAgent` directly. Step 2 says what that costs and what the port would look like.

---

## If you're comfortable, do this

Use this list if you want the route first. The full steps explain the trade-offs and help you recover when a step fails.

1. Add `Microsoft.Agents.AI` version `1.19.0` to `src/FinanceAssistant/FinanceAssistant.csproj`.
2. Delete all three files in one go: `src/FinanceAssistant/ChatAgent.cs`, `src/FinanceAssistant/Memory/ConversationStore.cs`, and `src/FinanceAssistant/Memory/SummarizingHistoryReducer.cs`. The reducer references the store directly, so they have to leave together.
3. In `Program.cs`, delete four things: the `var chatOptions = new ChatOptions { ... }` block, the `var store` line, the `var reducer` line, and the `[memory]` log line inside the REPL. The three deleted types have three call sites, not one, and the `chatOptions` block is simply dead once the agent owns its own tool list. Then construct the four tool objects as locals and replace the `var chatAgent = new ChatAgent(...)` line with `var agent = new ChatClientAgent(chatClient, instructions: systemPrompt, name: "FinanceAssistant", description: "Personal finance assistant", tools: [...])`. Add `using FinanceAssistant.Tools;` and `using Microsoft.Agents.AI;`. Remove `using FinanceAssistant.Memory;`.
4. Replace the REPL body's `chatAgent.RunTurnAsync(input)` with `agent.RunAsync(input, session)`, where `session` is created once via `await agent.CreateSessionAsync()`.
5. Run `dotnet run --project src/FinanceAssistant`. The REPL answers the same way it did on `p6-01-end` for every prompt except a transfer.

If you finish with time to spare, the stretch section at the bottom walks through running the agent with `RunStreamingAsync` and surfacing tokens to the console as they arrive.

---

## Step 1: Add the MAF package

Open `src/FinanceAssistant/FinanceAssistant.csproj`. Find the `<ItemGroup>` that holds the M.E.AI package references. Add one line:

```xml
<PackageReference Include="Microsoft.Agents.AI" Version="1.19.0" />
```

That's the only new package. It gives you `ChatClientAgent`, `AIAgent`, and `AgentSession`. Only the first is in `Microsoft.Agents.AI` itself. The other two live in `Microsoft.Agents.AI.Abstractions`, which comes along as a dependency. `Microsoft.Agents.AI` also depends on `Microsoft.Extensions.AI` at the exact `10.9.0` the repo already pins, so nothing is bumped and the rest of the M.E.AI surface keeps working.

Two things worth knowing:

**MAF reached 1.0 on 2 April 2026, and the 1.x line has held its promise.** We pin `1.19.0` because that's the version this demo was last walked end to end against. `1.20.0` is the current stable release on NuGet at the time of writing, and nothing in this demo changes if you bump to it. If you're demoing live, pin the version you tested.

**MAF is built on M.E.AI, not parallel to it.** The same `IChatClient` you registered in `ServiceCollectionExtensions.AddChatClient` is what `ChatClientAgent` wraps. The same `AIFunction` shape your tools produce via `AIFunctionFactory.Create` is what MAF dispatches. You're not rewriting the tools, the chat client, the embedding generator, or the database wiring. You're replacing the loop and the message buffer.

> There's no `CreateAIAgent` extension method on `IChatClient` in the 1.x line. An older preview surface had one, and it was renamed. The shipping equivalents are `chatClient.AsAIAgent(instructions, name, description, tools)` and `builder.BuildAIAgent(...)`. Both take the same arguments in the same order as the constructor, and both return a `ChatClientAgent`, which is an `AIAgent`. We use the explicit `new ChatClientAgent(...)` throughout, because Demo 2 constructs two agents side by side and the constructor makes the symmetry obvious.

---

## Step 2: Delete the hand-written loop

Delete three files, then remove the folder they leave behind:

```bash
rm src/FinanceAssistant/ChatAgent.cs
rm src/FinanceAssistant/Memory/ConversationStore.cs
rm src/FinanceAssistant/Memory/SummarizingHistoryReducer.cs
rmdir src/FinanceAssistant/Memory
```

Do not build yet. The build is red from here until you finish Step 3, and that is expected. When you do build, the compiler reports one error and stops:

```
Program.cs(3,24): error CS0234: The type or namespace name 'Memory' does not exist in the namespace 'FinanceAssistant'
```

It never gets far enough to report the three real call sites further down the file. Step 3 removes the `using` and the call sites together.

`ChatAgent.cs` held the `for` loop, the `GetResponseAsync` call, the `FunctionCallContent` enumeration, the `function.InvokeAsync` per tool call, the `ApprovalRequiredAIFunction` confirmation gate, and the iteration-cap fallback. All of that is now `ChatClientAgent`'s job.

`ConversationStore.cs` held `_messages`, `AppendSystemMessage`, `AppendUserMessage`, `AppendResponseMessages`, and `AppendToolResult`. All of that is now `AgentSession`'s job.

`SummarizingHistoryReducer.cs` has to go with the store. Its public surface is `TryReduceAsync(ConversationStore store, ...)`, so it stops compiling the moment `ConversationStore.cs` is deleted. There's no honest "leave it on disk for later" path. It's deletion or it's a port. The Pillar 4 summarising-history work targeted `ConversationStore`'s message list, and MAF's `AgentSession` owns its own message list through a different abstraction (`ChatHistoryProvider`). The right shape, when you want this back, is a custom `ChatHistoryProvider` that truncates and summarises as messages are appended. Truncation and summarisation are what that base class documents itself as being for. That's roughly thirty lines we won't write today, and the algorithm is still in `git show p6-01-end:src/FinanceAssistant/Memory/SummarizingHistoryReducer.cs` when you need it back.

### What this breaks, and why the solution build doesn't tell you

`tests/FinanceAssistant.Evals/AgentUnderTest.cs` constructs a real `ChatAgent`, a real `ConversationStore`, and a real `SummarizingHistoryReducer`, then stops the turn at the `ProposeNextStepAsync` seam. All three types are now gone, so the eval project can no longer compile.

You can't see that yet. `FinanceAssistant.Evals` has a `ProjectReference` to `FinanceAssistant`, the referenced project is the one that's currently red, and the compiler stops there. Come back and run this once Step 3 has the build green again:

```
$ dotnet build tests/FinanceAssistant.Evals/FinanceAssistant.Evals.csproj
AgentUnderTest.cs(1,24): error CS0234: The type or namespace name 'Memory' does not exist in the namespace 'FinanceAssistant'
Build FAILED. 1 Error(s)
```

`dotnet build FinanceAssistant.sln` reports zero errors at that same moment, because the eval project is not in the solution. That's a real gap in the scaffold and it's worth saying out loud in the room.

We leave the eval harness broken for this demo. Porting it isn't a rename. `ProposeNextStepAsync` exists so an eval can grade the first model response before any tool runs, and `ChatClientAgent` has no equivalent seam. It runs the whole loop or nothing. Getting Pillar 5's evals back means one of two things: grade the finished `AgentResponse` instead of the proposed step (cheaper to write, different thing being graded), or wrap the `IChatClient` in a recording decorator and grade the first response it sees. Both are worth doing. Neither belongs in a demo about the loop disappearing.

---

## Step 3: Rewrite Program.cs

Open `src/FinanceAssistant/Program.cs`. The seed-and-embed block at the top stays. The `ConfigurationBuilder` block stays. The DI registration stays. The embedding-backfill loop stays.

Everything from the `var chatOptions = new ChatOptions { ... }` block down to the end of the REPL gets replaced. That's lines 50 to 79 of the old file, and the block below is the whole replacement. Do not leave a blank line after the final `return 0;`.

```csharp
var systemPrompt = await File.ReadAllTextAsync(
    Path.Combine(AppContext.BaseDirectory, "Prompts", "SystemPrompt.md"));

// AgentToolset.CreateTools wraps TransferFundsTool in ApprovalRequiredAIFunction.
// We build the list here without that wrapper, so this demo stays about the loop.
var convertCurrency = new ConvertCurrencyTool();
var getTransactions = new GetTransactionsTool();
var searchTransactions = new SearchTransactionsTool(embedder);
var transferFunds = new TransferFundsTool();

var agent = new ChatClientAgent(
    chatClient,
    instructions: systemPrompt,
    name: "FinanceAssistant",
    description: "Personal finance assistant",
    tools:
    [
        AIFunctionFactory.Create(convertCurrency.Convert),
        AIFunctionFactory.Create(getTransactions.GetTransactions),
        AIFunctionFactory.Create(searchTransactions.SearchTransactions),
        AIFunctionFactory.Create(transferFunds.Transfer)
    ]);

var session = await agent.CreateSessionAsync();

Console.WriteLine("Finance assistant. Type a message, or 'exit' to quit.");

while (true)
{
    Console.Write("> ");
    var input = Console.ReadLine();
    if (input is null || string.Equals(input.Trim(), "exit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    var result = await agent.RunAsync(input, session);
    Console.WriteLine(result.Text);
}

return 0;
```

Now fix the `using` directives at the top of the file. Add two, keeping the list alphabetical. `using FinanceAssistant.Tools;` goes after `using FinanceAssistant.Data;`, and `using Microsoft.Agents.AI;` goes before `using Microsoft.EntityFrameworkCore;`:

```csharp
using FinanceAssistant.Tools;
using Microsoft.Agents.AI;
```

Then delete `using FinanceAssistant.Memory;`. That one isn't optional. Leaving it is the `CS0234` from Step 2.

`Microsoft.Extensions.AI` is already there and stays, because `AIFunctionFactory` and `IEmbeddingGenerator` both live in it. `using FinanceAssistant;` stays, because it's how `ServiceCollectionExtensions.AddChatClient` and `AddEmbeddingGenerator` resolve.

Build it. `dotnet build FinanceAssistant.sln` should be green again with zero warnings.

Five things worth reading carefully:

**We stopped calling `AgentToolset.CreateTools`.** Pillar 5 moved the tool list into `AgentToolset` so the REPL and the eval harness would grade the same set. That class is still on disk and still correct. We bypass it here for exactly one reason: it wraps `transferFunds` in `ApprovalRequiredAIFunction`, and we said we'd drop the gate for this demo. If you'd rather keep the wrapper, pass `tools: AgentToolset.CreateTools(embedder)` instead of the four-item list. The parameter type is `IList<AITool>`, which is what `CreateTools` already returns, so the whole `tools: [...]` argument collapses to that one call. Delete the four tool locals above it once you do, or they sit there unused. Read the approvals note in Troubleshooting first, because an approval request nobody answers looks like a hung agent.

**`ChatClientAgent` is the `AIAgent` for any `IChatClient`-backed model.** That's the bridge from M.E.AI to MAF. The same chat client your old `ChatAgent` called via `GetResponseAsync` is now wrapped inside the agent. The constructor owns the system prompt (passed as `instructions`), the name (used in handoff scenarios and trace output), a description (free-text label MAF surfaces in workflows), and the tool list.

**Tools are the same `AIFunction` instances.** `AIFunctionFactory.Create(method)` produces an `AIFunction` from any `[Description]`-decorated method. MAF accepts the same type M.E.AI accepts. If you wrote good `[Description]` attributes on the tools in Pillars 2 and 5, they keep working without edits. One of them now lies, and that's on us rather than on MAF. `TransferFundsTool.Transfer` still says "the user will be asked to confirm before it runs", and after this step nobody is. Fix the attribute text or put the wrapper back, but don't leave the model reading a promise the code no longer keeps.

**`AgentSession` replaces `ConversationStore`.** `await agent.CreateSessionAsync()` returns a session tied to this specific agent instance. The session accumulates messages across `RunAsync` calls. We construct it once at startup and reuse it for the lifetime of the REPL, which gives us a single multi-turn conversation. If you wanted N parallel users, you'd construct N sessions from the same agent.

**The loop disappeared.** There's no `for (var iteration = 1; iteration <= _maxIterations; iteration++)`. There's no `if (response.FinishReason != ChatFinishReason.ToolCalls) return`. There's no manual `function.InvokeAsync`. `ChatClientAgent` owns all of it: the agent loop, the tool-call detection, the tool invocation, the result appending, the next-turn dispatch.

That last one hides the single most surprising thing in this demo, so it's worth pulling out.

### Where the loop actually went

`AddChatClient` builds our `IChatClient` like this, and we haven't touched it:

```csharp
.AsBuilder()
.ConfigureOptions(o => o.Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None })
.Build()
```

There's no `UseFunctionInvocation` anywhere in the repo today, and the room put it there themselves. P2.01 added it (`Step 2: Add UseFunctionInvocation to the chat client`), P2.02 kept it, and P3.01 took it back out (`Step 1: Remove UseFunctionInvocation`) so the hand-written `ChatAgent` could own the dispatch. It's been absent since.

Tools still fire after Step 3. `ChatClientAgent` adds a `FunctionInvokingChatClient` to the pipeline itself when it doesn't find one, so the middleware the room deleted by hand in P3.01 is the middleware MAF quietly puts back. You can prove it with one line:

```csharp
Console.WriteLine(agent.GetService(typeof(FunctionInvokingChatClient)) is not null); // True
```

That's why the iteration cap didn't vanish with your `for` loop. It moved into a layer you already had, and the cap lives on `FunctionInvokingChatClient.MaximumIterationsPerRequest` rather than on the agent. To set it yourself, add the middleware explicitly in `ServiceCollectionExtensions.AddChatClient`:

```csharp
.AsBuilder()
.UseFunctionInvocation(configure: c => c.MaximumIterationsPerRequest = 8)
.ConfigureOptions(o => o.Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None })
.Build()
```

The default of 40 is generous for a workshop, so this is optional. Add it when you want the cap visible in the code that a reviewer reads, rather than implied by a decorator the framework inserted for you.

> The `RunAsync` return type is `AgentResponse`. It has `.Text` (the model's final natural-language answer), `.Messages` (everything that landed in this turn, including any tool-call and tool-result pairs), and a handful of others: `.FinishReason`, `.Usage`, `.AgentId`, `.ResponseId`, `.ContinuationToken`. For the REPL we only need `.Text`. To see the tool calls that fired during a turn, iterate `result.Messages` and filter on `m.Contents.OfType<FunctionCallContent>()`.

---

## Step 4: Run it

From the repo root:

```bash
dotnet run --project src/FinanceAssistant
```

The REPL header is unchanged:

```
Finance assistant. Type a message, or 'exit' to quit.
>
```

The per-turn console noise is not. Three lines from `p6-01-end` are gone: the `[memory] N messages in history` line that `Program.cs` printed before each turn, and two that `ChatAgent` printed inside the loop, `[agent] iteration N: calling foo, bar` and `[agent] iteration N: final answer`. All three were our own `Console.WriteLine` calls, and all three left with the code that made them. MAF can emit its own per-turn trace through `ILogger`, in a different format. See Troubleshooting for what that costs to switch on.

Try the four prompts you tried at the end of Pillar 6, in order. The answers should match.

1. `Convert 100 EUR to USD.` The ConvertCurrency tool fires, returns a string, and the agent paraphrases it.
2. `List my transactions on 2026-01-09.` The GetTransactions tool fires with `dateExpression="2026-01-09"` and returns the two rows the seed CSV has on that date. Pick another single-day date if you reseeded with different data. Not every day in the seed has transactions, and a date with zero rows produces an empty but valid tool result.
3. `Find coffee shop purchases.` The SearchTransactions tool fires and the agent narrates the matches. `topK` defaults to 5, but the model picks the value and it's allowed up to 20, so do not be surprised by a long list.
4. `What's the biggest transaction in January 2026?` GetTransactions runs over the range, then the model picks the biggest from the list. Two model calls, both inside one `RunAsync`.

Then ask a follow-up. `And what about 50 GBP?` after the first prompt is enough. The agent carries the context across the call. That's the session doing its job. The hand-written `ConversationStore` did the same thing, and the difference is that you didn't have to write it.

---

## Step 5: Diff what just happened

Your work is still uncommitted, so diff a single revision against the working tree. Pass one revision, not two:

```bash
git diff --stat p6-01-end
```

`git diff --stat HEAD` prints exactly the same thing, because `HEAD` is `p6-01-end` and one-revision `git diff` always compares that revision to your working tree. The form to avoid is `git diff --stat p6-01-end HEAD`. Two revisions means git compares the commits to each other and ignores your uncommitted work, and since those two revisions are the same commit, it prints nothing at all.

```
 src/FinanceAssistant/ChatAgent.cs                  | 147 ---------------------
 src/FinanceAssistant/FinanceAssistant.csproj       |   1 +
 src/FinanceAssistant/Memory/ConversationStore.cs   |  65 ---------
 .../Memory/SummarizingHistoryReducer.cs            |  66 ---------
 src/FinanceAssistant/Program.cs                    |  38 ++++--
 5 files changed, 26 insertions(+), 291 deletions(-)
```

278 lines of hand-written agent machinery deleted, 1 line added to the csproj, and the behaviour is the same. That's the trade MAF asks you to make: hand over the loop, the session, and the tool dispatch in exchange for less code, an agent type that composes into workflows, and a session type the framework can serialise.

Notice what `Program.cs` did. It went from 79 lines to 91. The file that lost the most conceptual weight got twelve lines longer, because the tool construction that used to live behind `AgentToolset` is now inline and the `ChatClientAgent` constructor is spread across ten lines for readability. Codebases shrink when you delete an abstraction. Individual files often don't. If someone in the room is keeping score on `Program.cs` alone, they'll conclude MAF cost you lines, and on that one file they're right.

What you give up is also worth naming, and it's less than it looks. The summarising-history reducer is gone, and it comes back as a custom `ChatHistoryProvider` if you need it. The two console log lines are gone. The eval harness is broken until someone ports `AgentUnderTest`. The `ApprovalRequiredAIFunction` gate is out of this REPL because we removed it, not because MAF can't express it.

The pattern this enables matters more than the lines saved. With a `ChatClientAgent` instance, you can pass it to `AgentWorkflowBuilder.CreateHandoffBuilderWith(agent)`, to group-chat orchestration, to graph workflows, and to checkpointing. Every one of those takes an `AIAgent`, not a hand-written class. Demo 2 picks up from here with the handoff workflow.

---

## Stretch goal: stream the response

`RunAsync` returns one big `AgentResponse` when the model is done. For a REPL that's fine. For anything user-facing you usually want to surface tokens as they arrive. MAF supports this via `RunStreamingAsync`.

Replace these two lines:

```csharp
var result = await agent.RunAsync(input, session);
Console.WriteLine(result.Text);
```

with this loop:

```csharp
await foreach (var update in agent.RunStreamingAsync(input, session))
{
    Console.Write(update.Text);
}
Console.WriteLine();
```

`RunStreamingAsync` returns an `IAsyncEnumerable<AgentResponseUpdate>`. Each update carries a delta. `update.Text` is the partial token text the model just produced, or empty when the update is a tool call rather than text. You can also filter on `update.Contents` to render tool calls inline ("calling get_transactions...") which is closer to what a chat UI would do.

The session still accumulates correctly under streaming. After the loop completes, the full assistant turn is in the session, the same as if you'd called `RunAsync`.

> Streaming changes the latency story but not the cost story. The tool calls still happen in the middle of the stream, and the model still pays for every token it produces, streamed or not. The win is that the user sees the first token in about 300ms instead of waiting for the full turn.

---

## Troubleshooting

### Build fails with "The type or namespace name 'Agents' does not exist in the namespace 'Microsoft'"

The `using Microsoft.Agents.AI;` line is what fails first, so this is the only error you get. `Microsoft.Agents.AI` either isn't in the csproj or `dotnet restore` hasn't picked it up. Confirm the `PackageReference` is inside an `<ItemGroup>` and not orphaned. Then run `dotnet restore`. If that still fails, your local NuGet feed may not have the package. Check that `nuget.org` is in the sources listed by `dotnet nuget list source`.

### Build fails with "The name 'convertCurrency' does not exist in the current context"

You pasted the Step 3 code block without the four tool constructions above it. At `p6-01-end` those objects are built inside `AgentToolset.CreateTools`, not in `Program.cs`, so the locals don't exist until you declare them. Copy the whole block, including the four `new ...Tool()` lines.

### Build fails with "The type or namespace name 'ConvertCurrencyTool' could not be found"

Add `using FinanceAssistant.Tools;`. The tool classes live in that namespace, and `Program.cs` never needed it before because `AgentToolset` did the constructing.

### `agent.RunAsync` returns the same answer every time, or context is lost between turns

You are probably constructing a new session on every loop iteration. The session is what carries history. Construct it once via `await agent.CreateSessionAsync()` before the `while (true)` loop, not inside it. The pattern is one agent, one session for the conversation.

### Tools fire but the agent never produces a final answer

The symptom is an empty `result.Text` with `result.FinishReason` set to `tool_calls`. MAF caps tool iterations like the hand-written loop did, and the cap lives on `FunctionInvokingChatClient.MaximumIterationsPerRequest`, which defaults to 40. Set it with `UseFunctionInvocation(configure: c => ...)` when you build the chat client, as shown at the end of Step 3.

You get here when the model keeps asking for tools and never settles. That usually means a tool is handing back something it can't act on: an empty result it reads as "try again", or a shape that doesn't answer the question it asked. A tool that throws doesn't get you here. See "A tool throws and the agent apologises instead of failing" below for that case.

Do not look for the cap on `ChatClientAgentRunOptions`. That's the per-run options object you can pass as the third argument to `RunAsync`, and it carries `ChatOptions` and a `ChatClientFactory` of its own, plus `ResponseFormat`, `ContinuationToken`, `AllowBackgroundResponses`, and `AdditionalProperties` inherited from `AgentRunOptions`. The cap is on none of them.

`GetTransactionsTool` is the first place to look. The safe-fallback shape we built in Pillar 2 exists so that a bad date or an empty range comes back as a result the model can read and stop on, rather than as nothing at all.

### You want to see what the agent does per turn

`ChatClientAgent` takes an `ILoggerFactory`. It's the sixth parameter of the same constructor Step 3 already uses, so reach it with a named argument after `tools:`, not with a different overload:

```csharp
loggerFactory: LoggerFactory.Create(b => b.AddConsole())
```

Two things have to be in place first, and neither is in the repo today. Add `using Microsoft.Extensions.Logging;` to `Program.cs`, and add the console provider package to `src/FinanceAssistant/FinanceAssistant.csproj`:

```xml
<PackageReference Include="Microsoft.Extensions.Logging.Console" Version="10.0.11" />
```

Without that package there's no `AddConsole` to call, and the compiler says so: `error CS1061: 'ILoggingBuilder' does not contain a definition for 'AddConsole'`. The quieter trap is what happens if you route around that error with `LoggerFactory.Create(b => { })`. A factory with no providers logs to nowhere, reports nothing, and costs an easy twenty minutes.

### A tool throws and the agent apologises instead of failing

Nothing propagates out of `RunAsync`. `FunctionInvokingChatClient` catches whatever your tool throws and hands the model a tool result reading `Error: Function failed.`, so the model sees a failure it can narrate but you see no stack trace. Note that it's the middleware doing this, not the factory. Call `AIFunctionFactory.Create(...).InvokeAsync(...)` directly and the exception comes straight back at you, which is the quickest way to find out what actually broke.

One exception is worth knowing about. `FunctionInvokingChatClient.MaximumConsecutiveErrorsPerRequest` defaults to 3, so a tool that fails three times in a row does throw out of `RunAsync`. A single failure the model recovers from never will.

Returning `null` from a tool is fine and doesn't throw. The four existing tools all return an object anyway, and that's the shape to copy when you add a fifth.

### The system prompt isn't being honoured

Confirm the `instructions:` parameter on the `ChatClientAgent` constructor is set to the full text of `SystemPrompt.md`, not the path. It's easy to copy the wrong line.

Do not go looking for a system message in the session, and do not expect a fresh session to help. MAF doesn't put instructions in the message list at all. It sends them as `ChatOptions.Instructions` on every single request, which is what the constructor's own documentation means by "provided to the `IChatClient` with each invocation". The session holds your user and assistant turns, plus the tool-call and tool-result pairs from any turn that used a tool. What it never holds is a system message. If the prompt text loads correctly and the model still ignores it, the problem is the prompt or the model, and a new session won't change either.

### Transfer prompts no longer ask for confirmation

Expected, because Step 3 dropped the wrapper. It's not a limitation of MAF.

Approvals are an agent-level concern in `1.19.0`. `ApprovalRequiredAIFunction` is still the M.E.AI type you wrap a tool in, and `ChatClientAgent` handles the rest by default. It injects an approval-binding decorator as the outermost layer of its chat-client pipeline, which records every `ToolApprovalRequestContent` the framework surfaces and binds your `ToolApprovalResponseContent` back to the right tool call on the next turn. `ToolApprovalRequestContent` and `ToolApprovalResponseContent` are the two types you touch, and both sit in the `Microsoft.Extensions.AI` namespace (shipped in `Microsoft.Extensions.AI.Abstractions`), not in MAF. The decorator itself is internal, so you can observe its behaviour but you can't name it in your code.

For "don't ask me again" behaviour and for queuing several approvals one at a time, there's dedicated middleware: `agentBuilder.UseToolApproval(new ToolApprovalAgentOptions { ... })`. The options object carries `AutoApprovalRules`, typed `IEnumerable<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>>`, plus `MaxAutoApprovalIterations`. A rule is a plain lambda over a `ToolAutoApprovalRuleContext`.

We're not wiring it here, because this demo is about the loop disappearing and one idea per diff is the rule. To get the gate back today, pass `tools: AgentToolset.CreateTools(embedder)` in Step 3 to restore the Pillar 5 wrapper, then read `result.Messages` for `ToolApprovalRequestContent` and send back a `ToolApprovalResponseContent` on the next `RunAsync`. Until you do that second half, a transfer prompt looks like a hang: the agent returns an approval request and `result.Text` is empty.

If an ungated `TransferFundsTool` is dangerous in your environment, remove it from the `tools:` list.

---

## You can now

Take any M.E.AI-based agent and migrate it to MAF in under an hour: add one package, delete your hand-written loop, construct a `ChatClientAgent` from the same `IChatClient`, pass the same `AIFunction` instances as tools. The agent and session types you end up with compose into the rest of MAF (workflows, handoff, group chat, checkpointing), and the M.E.AI primitives underneath stay where they are.

The mental model is layers. Tools and chat clients live in M.E.AI. Agents, sessions and orchestrations live in MAF. M.E.AI is the "what" (a tool, a model call). MAF is the "who" (an agent, a workflow). Demo 2 picks up the second half.

---

## Summary

You've changed:

- **`FinanceAssistant.csproj`**: one new `PackageReference` for `Microsoft.Agents.AI` (`1.19.0`).
- **`ChatAgent.cs`**: deleted. The loop, the tool-call detection, the tool invocation, and the iteration cap all live in `ChatClientAgent` now.
- **`Memory/ConversationStore.cs`**: deleted. The session owns the message buffer.
- **`Memory/SummarizingHistoryReducer.cs`**: deleted. It referenced `ConversationStore` directly, so it had to go with it. The algorithm is preserved in git on the `p6-01-end` branch, ready to come back as a custom `ChatHistoryProvider`.
- **`Program.cs`**: rewritten REPL. The four tools are constructed inline, `new ChatClientAgent(chatClient, instructions, name, description, tools)` builds the agent, `await agent.CreateSessionAsync()` is called once, and `agent.RunAsync(input, session)` replaces the per-turn loop.

What survived intact:

- All four tool classes in `Tools/`. The code is unchanged, though `TransferFundsTool`'s `[Description]` now promises a confirmation that no longer happens.
- `SystemPrompt.md`, unchanged.
- `AgentToolset.cs`, unchanged and still on disk, no longer called by the REPL.
- `ServiceCollectionExtensions.AddChatClient` and `AddEmbeddingGenerator`, unchanged.
- The pgvector container, the seed CSV, and the embedding backfill.

What's broken and left that way on purpose:

- `tests/FinanceAssistant.Evals` does not compile. `AgentUnderTest` needs a port to `ChatClientAgent`, and `dotnet build FinanceAssistant.sln` won't warn you, because that project is not in the solution.

---

## Additional Resources

- [Microsoft Agent Framework Overview](https://learn.microsoft.com/en-us/agent-framework/overview/): conceptual map of agents, sessions, workflows.
- [`ChatClientAgent` class reference](https://learn.microsoft.com/en-us/dotnet/api/microsoft.agents.ai.chatclientagent?view=agent-framework-dotnet-latest): constructor overloads, options, lifecycle. The `-latest` view tracks the newest release rather than the `1.19.0` we pin, so check the version selector if a member looks unfamiliar.
- [Multi-turn conversations](https://learn.microsoft.com/en-us/agent-framework/user-guide/agents/multi-turn-conversation): how sessions carry history across `RunAsync` calls.
- [Upgrading to MAF in your .NET AI chat app](https://devblogs.microsoft.com/dotnet/upgrading-to-microsoft-agent-framework-in-your-dotnet-ai-chat-app/): the migration path Microsoft recommends.

Next: Demo 02 splits this single agent into two specialists (DataAnalyst, MoneyCoach) and wires them up with MAF's handoff orchestration. The work you did here is the foundation, and the workflow piece sits on top.
