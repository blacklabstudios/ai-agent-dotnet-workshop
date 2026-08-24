# Demo 02 - Split the Agent Into a Data Analyst and a Money Coach

> Lives at `docs/demos/p6-02-multi-agent-handoff.md`. Builds on `p6-01-migrate-to-agent-framework.md`. Facilitator drives, room watches, nobody types.

## Mission

Split the `FinanceAssistant` from Demo 1 into two agents that hand work to each other. `DataAnalyst` lists transactions, searches them, and converts currencies. It does not give advice. `MoneyCoach` interprets findings and suggests budgets. It has no database tools. The handoff workflow keeps one conversation for the user.

**Learning Objectives**:

- Declare a handoff topology with `AgentWorkflowBuilder` and `WithHandoffs`
- Keep two specialists on one conversation
- Run a workflow with `InProcessExecution.RunStreamingAsync` and `TurnToken`
- Choose between handoff, agent-as-tool, and group chat

---

## Prerequisites

- Demo 01 finished, on whatever branch you ran it on. If you need the canonical end-state, it's `demo-p6-maf~1` (the `-end` branches stop at `p6-01-end`, which is where Demo 01 *starts*). `dotnet build` is green and `dotnet run --project src/FinanceAssistant` still runs the single-agent REPL.
- Azure OpenAI user-secrets still wired (no changes from Demo 01).
- The pgvector container is healthy.

---

## What we're solving

Demo 1 left one agent with four tools. That is enough for most finance questions. One agent can convert a currency or list transactions in one turn.

Use more than one agent only when the responsibilities genuinely conflict. Data analysis and financial coaching have different instructions, tools, and evaluation criteria. A single agent with both jobs can underperform at each.

> The caveat we put on the Pillar 6 forward-look slide still applies. Multi-agent is only worth the cost when the responsibilities really do pull apart and a single agent measurably underperforms. The token cost is roughly 10x to 15x a single-agent baseline, and the topology adds emergent failure modes (one agent loops, the other gives up, neither tells you why). Reach for this when you've measured a single-agent ceiling, not before. The demo is here so you can see the shape.

Build two `ChatClientAgent` instances from the same `IChatClient`. Give them different tools and prompts. Then join them with a handoff topology. MAF adds a `handoff_to_<agent_id>` function for each outgoing edge. When one agent calls it, the other takes the turn and sees the same conversation history (apart from tool-call internals).

Three pieces:

1. **Split the tool surface.** `DataAnalyst` gets `GetTransactions`, `SearchTransactions`, and `ConvertCurrency`. `MoneyCoach` gets no tools at first. We'll talk about the deliberately-omitted `TransferFundsTool` at the bottom.
2. **Author two system prompts.** Short, specialised, and explicit about when each agent should hand off.
3. **Build the workflow.** `AgentWorkflowBuilder.CreateHandoffBuilderWith(coach).WithHandoffs(coach, [analyst]).WithHandoffs(analyst, [coach]).Build()`. The REPL changes shape: instead of calling `agent.RunAsync`, we feed messages into the workflow and watch its event stream.

---

## If you're comfortable, do this

Use this list for the route first. Items 1 and 2 both belong to Step 1. The label on each item points to its detailed step. Step 6 only inspects the diff.

1. *(Step 1.1)* Update `src/FinanceAssistant/FinanceAssistant.csproj`: add `<PackageReference Include="Microsoft.Agents.AI.Workflows" Version="1.19.0" />` and glob the prompts copy-to-output entry to `<None Update="Prompts\*.md">`. No warning suppression needed: the handoff workflow APIs are stable as of `1.19.0`.
2. *(Step 1.2)* Create `src/FinanceAssistant/Prompts/AnalystPrompt.md` and `src/FinanceAssistant/Prompts/CoachPrompt.md`. Short, specialised. Copy the bodies from Step 1 below verbatim: the Analyst's "two moves" paragraph is doing more work than it looks like.
3. *(Step 2)* In `Program.cs`, construct two agents with `new ChatClientAgent(...)`: `analyst` (name `data_analyst`, three data tools) and `coach` (name `money_coach`, no tools). Drop the `SystemPrompt.md` file read, the two-line `// AgentToolset.CreateTools ...` comment above the tools, the `transferFunds` tool, and the `CreateSessionAsync` line, then delete `Prompts/SystemPrompt.md` itself.
4. *(Step 3)* Build the workflow: `AgentWorkflowBuilder.CreateHandoffBuilderWith(coach).WithHandoffs(coach, [analyst]).WithHandoffs(analyst, [coach]).Build()`.
5. *(Step 4)* Replace everything from the banner line to `return 0;`. For each user turn, append a `ChatMessage` to a running `List<ChatMessage>`, call `InProcessExecution.RunStreamingAsync(workflow, messages)`, send a `TurnToken(emitEvents: true)`, watch the event stream for `AgentResponseUpdateEvent` (per-token output) and `WorkflowOutputEvent` (turn complete), and update the running message list from the workflow's final output.
6. *(Step 5)* `dotnet run --project src/FinanceAssistant`. Ask "how much did I spend eating out in January 2026" and watch the coach hand off to the analyst and the analyst search and total. Whether the analyst then hands back for interpretation is a coin toss, so don't promise the room it will. Step 5 has the detail, and it's the part worth reading even if nothing else here needed you.

If you finish with time to spare, the stretch section below covers where MAF's approval surface moved to between `1.6.1` and `1.19.0`, and why that move is the most useful thing this demo has to say about building on a young framework.

---

## Step 1: Add the Workflows package and prompts

Two small edits to `src/FinanceAssistant/FinanceAssistant.csproj`. They sit in the same file so it's easier to do them together up front rather than thread them through Steps 2 and 3.

**1.1: Add the Workflows package.** Demo 1 brought in `Microsoft.Agents.AI`, which ships `ChatClientAgent` and `AgentSession`. The handoff orchestration primitives (`AgentWorkflowBuilder`, `InProcessExecution`, `StreamingRun`, `TurnToken`, the `*Event` types) ship in a separate package, `Microsoft.Agents.AI.Workflows`. Everything we reference from this package lives under the `Microsoft.Agents.AI.Workflows` namespace. Step 3 adds the `using` directive, where the first of those types appears. Add a second `PackageReference` to the same `<ItemGroup>` you used in Demo 1:

```xml
<PackageReference Include="Microsoft.Agents.AI.Workflows" Version="1.19.0" />
```

> Coming from the stated prerequisite there's nothing else to do here. **Only if you followed an older version of this demo**, delete the `<NoWarn>$(NoWarn);MAAIW001</NoWarn>` line it had you add: the handoff APIs were experimental in the early 1.x minors and this repo runs `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, so consuming them used to break the build. They are stable in `1.19.0` and the whole demo compiles clean with no suppression. Worth thirty seconds in the room: a whitelisted warning code tends to outlive the reason it was added.

**1.2: Glob the prompts copy-to-output entry.** We author two new `.md` files alongside `SystemPrompt.md` a few lines below. Easiest path is to replace the existing `<None Update="Prompts\SystemPrompt.md">` block with a glob so all the prompt files come along automatically. This is a swap, not a second block.

Find this in the csproj:

```xml
<None Update="Prompts\SystemPrompt.md">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</None>
```

Replace it with:

```xml
<None Update="Prompts\*.md">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</None>
```

Now author the two prompts. Each is opinionated about its own job and explicit about when the other agent should take over.

Create `src/FinanceAssistant/Prompts/AnalystPrompt.md`:

```
You are the Data Analyst. You investigate the user's finances using the tools
you have: list transactions in a date range, search transactions by topic,
convert currencies. You produce factual, sourced answers with numbers.

You do not give advice. You do not recommend budgets, savings targets, or
behavioural changes. If the user asks for advice, interpretation, or "what
should I do", hand the conversation to the Money Coach.

Be concise. Numbers are exact when sourced from a tool. Cite the date range
or query you used so the Coach (or the user) can verify your work.

Always answer in two moves. First write a short `Findings:` block with the
numbers and the date range or query you used. Then hand the conversation back
to the Money Coach so they can interpret it. Never end a turn yourself after
answering a data question.
```

Create `src/FinanceAssistant/Prompts/CoachPrompt.md`:

```
You are the Money Coach. You help the user think about spending, saving, and
financial trade-offs. You explain your reasoning, ask clarifying questions
when the goal is ambiguous, and propose concrete next steps.

You have no tools of your own. When you need data about the user's actual
transactions (totals, lists, fuzzy searches by topic, currency conversions),
hand the conversation to the Data Analyst. Wait for the Analyst to come back
with numbers before you interpret.

When the Analyst returns with numbers, do not stay silent. Always restate the
key figure in plain language, offer one or two interpretations or trade-offs
the user might care about, and propose a concrete next step. The user should
finish every data-driven turn with both the number and your reading of it.

Lead with empathy, follow with specifics. Disagree with the user when the
data warrants it. When you don't have enough information, ask, don't guess.
```

Four things worth reading carefully:

**The prompts are explicit about handoff triggers.** "If the user asks for advice, hand to the Coach." "When you need data, hand to the Analyst." MAF's handoff orchestration injects a hidden handoff function on each agent based on the `WithHandoffs` rules, and it injects its own short system instruction telling the model that handoff functions exist. What it doesn't do is decide *when* your particular agents should use them. These two paragraphs are doing that work.

**The Coach has no tools on purpose.** The temptation is to give every agent every tool "just in case". That defeats the split. The Coach is the agent the user talks to most often. The Analyst is the specialist it consults. If the Coach could list transactions on its own, the topology collapses and you're back to one agent with a costlier inference pattern. Keep the tool sets disjoint.

**The Analyst's "two moves" paragraph does two jobs, and only one of them is reliable.** The default behaviour of this topology is that the workflow ends the moment the question is factually answered: the Analyst answers, nothing hands back, and the Coach never gets to coach. Asking the *Analyst* to hand back is the right place to intervene, because the Analyst is the agent holding the turn when that decision gets made. Asking the Coach to come back is asking an agent that isn't holding the turn.

The `Findings:` half works every time. You'll get a labelled, sourced block on every data question. The hand-back half is a coin toss, and Step 5 shows you both faces of it. Do not promise the room three turns.

**The Coach's "always interpret" paragraph shapes the third turn, it doesn't cause it.** When the hand-back does happen, this is what makes the Coach restate the figure and add a next step instead of re-printing the Analyst's table. When it doesn't happen, no wording in `CoachPrompt.md` rescues it.

One side effect worth knowing before you see it on a projector, and the most interesting failure in the demo: MAF's injected instruction tells agents never to narrate handoffs, and our Analyst paragraph pushes the other way. So the Analyst frequently signs off with "I'm handing this back to the Money Coach for interpretation" **and then doesn't call the handoff function at all**. It narrates the intent and terminates. Instruction-following split clean down the middle between the sentence and the tool call, in front of the room, which is a better lesson about prompting than a clean run would have been.

---

## Step 2: Construct the two agents

Back in `Program.cs`, the seed-and-embed block at the top stays. The DI block stays. Everything below them changes.

The edit is a single contiguous replacement. Select from this line:

```csharp
var systemPrompt = await File.ReadAllTextAsync(
```

down to and including this one:

```csharp
var session = await agent.CreateSessionAsync();
```

and replace the lot with the block below. That's 24 lines, so here's exactly what should be inside your selection, to check before you delete it:

- the `SystemPrompt.md` file read (the file itself is deleted at the end of this step),
- the two-line comment Demo 1 left behind, verbatim:

  ```csharp
  // AgentToolset.CreateTools wraps TransferFundsTool in ApprovalRequiredAIFunction.
  // We build the list here without that wrapper, so this demo stays about the loop.
  ```

- the four tool constructions, which become three: `transferFunds` goes (we discuss it at the bottom),
- the single `agent` construction, which becomes two,
- `var session = await agent.CreateSessionAsync();`, because workflows don't take an `AgentSession`. What replaces it is a plain `List<ChatMessage>` that you own and pass in whole on every turn, and Step 4 builds it.

Nothing above `var systemPrompt` moves, and the `while (true)` REPL below `var session` stays where it is until Step 4.

```csharp
var analystPrompt = await File.ReadAllTextAsync(
    Path.Combine(AppContext.BaseDirectory, "Prompts", "AnalystPrompt.md"));
var coachPrompt = await File.ReadAllTextAsync(
    Path.Combine(AppContext.BaseDirectory, "Prompts", "CoachPrompt.md"));

var convertCurrency = new ConvertCurrencyTool();
var getTransactions = new GetTransactionsTool();
var searchTransactions = new SearchTransactionsTool(embedder);

var analyst = new ChatClientAgent(
    chatClient,
    instructions: analystPrompt,
    name: "data_analyst",
    description: "Specialist in transaction data and currency conversion",
    tools:
    [
        AIFunctionFactory.Create(convertCurrency.Convert),
        AIFunctionFactory.Create(getTransactions.GetTransactions),
        AIFunctionFactory.Create(searchTransactions.SearchTransactions)
    ]);

var coach = new ChatClientAgent(
    chatClient,
    instructions: coachPrompt,
    name: "money_coach",
    description: "Financial coach who interprets findings and recommends next steps",
    tools: []);
```

Now delete `src/FinanceAssistant/Prompts/SystemPrompt.md`. Nothing reads it any more, and the glob from Step 1.2 would otherwise keep it in scope as a build item. (MSBuild does clean the stale copy out of `bin/` on the next build, so this is tidiness rather than a broken output.)

> One thing that deletion touches outside this project: `tests/FinanceAssistant.Evals/AgentUnderTest.cs` reads `Prompts/SystemPrompt.md` out of its own output directory, where the project reference puts it. That project is already broken at this point in the workshop (Demo 1 deleted `src/FinanceAssistant/Memory/`, which it also references) and it isn't in `FinanceAssistant.sln`, so neither break shows up in `dotnet build` from the repo root. Worth knowing if you go back to Pillar 5's evals after the demos: they need the split prompts wired in, not just the old file restored.

Two things worth knowing:

**`name` and `description` are both load-bearing, and `description` more so.** MAF doesn't name the handoff function after the agent. It registers one function per outgoing edge, named `handoff_to_<agent_id>` where the id indexes that agent's own outgoing edges. Both of our agents have exactly one, so both call `handoff_to_1`: the Coach to reach the Analyst, the Analyst to reach the Coach. The names aren't unique across the workflow, which is worth knowing before you try to read a trace by tool name alone. The model chooses a target from the function's *description*, which MAF derives from the target agent's `description`, falling back to its `name` and then its instructions. Build a workflow whose target agent has none of the three and it throws at build time rather than guessing. So write `description:` as the sentence that tells another agent when to pick this one. `name` still matters: it is what you see in workflow events (`ExecutorId`) and trace output, which is how you read the demo on screen. `data_analyst` and `money_coach` are fine. `Agent1` and `Agent2` are not.

**Both agents share the same `chatClient`.** You're not paying for two deployments. One Azure OpenAI deployment, two agents pointed at it. The cost difference between this and Demo 1 is the extra tokens (each handoff replays the relevant history into the new agent's context), not extra infrastructure.

---

## Step 3: Build the handoff workflow

First the `using` directive at the top of the file, since everything below depends on it:

```csharp
using Microsoft.Agents.AI.Workflows;
```

Then the workflow builder, just below the agent constructions:

```csharp
var workflow = AgentWorkflowBuilder
    .CreateHandoffBuilderWith(coach)
    .WithHandoffs(coach, [analyst])
    .WithHandoffs(analyst, [coach])
    .Build();
```

Three things worth reading carefully:

**`CreateHandoffBuilderWith(coach)` makes the Coach the entry point.** The first user message goes to the Coach. From there, the Coach decides whether to answer directly or hand off. If you want the Analyst to be the front door (more useful when the typical question is a data question), swap them.

**`WithHandoffs(coach, [analyst])` declares a one-way edge.** Coach can transfer to Analyst. We add `WithHandoffs(analyst, [coach])` so the Analyst can return the conversation. Without the return edge, the Analyst would be a one-way trap: once you transfer in, you can't transfer back.

**The framework injects the handoff functions.** You didn't write a handoff tool, and you won't see one in your code. MAF inspects the topology you declared, registers a hidden `handoff_to_<agent_id>` function per outgoing edge, and prepends a short system instruction to each agent explaining that these functions exist and that handoffs should never be narrated to the user. The agent hands off by calling one of those functions, and the framework intercepts the call and swaps executors. From the model's perspective, handing off looks like calling any other tool.

> **If you build here, it will fail, and that is the correct result.** The old REPL below still calls `agent.RunAsync(input, session)` and Step 2 deleted both of those locals. Building now is in fact a useful checkpoint, as long as you know what you're looking for. You want exactly two errors, around line 97:
>
> ```
> Program.cs(97,24): error CS0103: The name 'agent' does not exist in the current context
> Program.cs(97,46): error CS0103: The name 'session' does not exist in the current context
> ```
>
> Two `CS0103`s naming `agent` and `session`, and nothing else, means Step 2 went in correctly. Any other error means it didn't, and the Step 2 selection is the place to look. Step 4 makes it green again.

> One name to know if you ever break the chain apart and hold the builder in a typed local, which the code above deliberately does not. `CreateHandoffBuilderWith` returns a `HandoffWorkflowBuilder`, and `1.19.0` also ships a near-identically named `HandoffsWorkflowBuilder` (with an `s`) marked `[Obsolete]` in favour of it. Under this repo's `TreatWarningsAsErrors`, typing the wrong one turns a one-letter slip into a failed build.

> Adding a third agent later is a matter of declaring it and adding the right `WithHandoffs` edges. The topology can be a star (one triage agent hands to N specialists), a mesh (every agent can transfer to every other), or a chain (sequential). For two agents the question of topology barely registers. For five it's the architecture diagram.

> **Builder options worth knowing about, that we're deliberately not using.** The handoff builder in `1.19.0` carries several levers this demo leaves at their defaults, and two of them are the honest answer to a problem you'll hit in Step 5.
>
> - **`WithTerminationCondition(...)`** takes a predicate over the conversation and decides when the workflow stops. The default is to stop as soon as the question is factually answered, which is why the Coach's closing turn has to be coaxed back by prompt wording (the Analyst's "two moves" paragraph from Step 1). This is the structural version of that fix, and despite having two overloads (`Func<IReadOnlyList<ChatMessage>, bool>` and its `ValueTask<bool>` sibling) a bare lambda binds without a cast, so it is cheap to try live: `.WithTerminationCondition(msgs => msgs.Count > 6)` compiles as-is. Not a good termination rule, but a good five seconds in the room.
> - **`EnableReturnToPrevious()`** expresses "hand back to whoever called me" without declaring the reverse edge by hand. We keep the explicit `WithHandoffs(analyst, [coach])` because the second edge makes the topology visible in the code, which is the point of a demo.
> - **`WithHandoffInstructions(...)`** injects shared guidance into every agent's handoff tooling, so you're not repeating transfer etiquette in each prompt file.
> - **`WithAutonomousMode(...)`** lets the agents keep taking turns without waiting on the user, bounded by a turn limit (50 by default) and a continuation prompt (`"User did not respond. Continue assisting autonomously."`). It's the other structural answer to the missing-third-turn problem, and a much bigger hammer than a termination condition.
> - **`WithToolCallFilteringBehavior(...)`** controls which tool-call traffic crosses a handoff, which is the knob behind the "context broadcast is best-effort" caveat in Troubleshooting.
> - **`EmitAgentResponseUpdateEvents(bool)` and `EmitAgentResponseEvents(bool)`** control which events reach your stream, which matters once the event loop is doing more than printing. They chain like the others above. If you go looking for them as options instead, you'll find same-named properties on `HandoffAgentExecutorOptions`, which is `internal` and not a route you can take: chain the builder methods.
>
> None of these existed in the shape they have today when this demo was first written. That's worth a sentence in the room: the framework got better at the thing the demo works around.

---

## Step 4: Drive the workflow from the REPL

This is the biggest change. Workflows aren't invoked like agents. Instead of `agent.RunAsync(input, session)`, you feed messages into the workflow and watch its event stream. The pattern in MAF's docs is the one we'll use here.

Everything from Demo 1's banner line to the end of the file goes, `return 0;` included. The line to select from is this one, which is *not* the same string as the one in the replacement block below (the new banner says "multi-agent"):

```csharp
Console.WriteLine("Finance assistant. Type a message, or 'exit' to quit.");
```

Replace from there to end of file with:

```csharp
Console.WriteLine("Finance assistant (multi-agent). Type a message, or 'exit' to quit.");

List<ChatMessage> messages = new();

while (true)
{
    Console.Write("> ");
    var input = Console.ReadLine();
    if (input is null || string.Equals(input.Trim(), "exit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    messages.Add(new ChatMessage(ChatRole.User, input));

    await using StreamingRun run = await InProcessExecution.RunStreamingAsync(workflow, messages);
    await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

    string? lastExecutorId = null;
    List<ChatMessage> newMessages = new();

    await foreach (WorkflowEvent evt in run.WatchStreamAsync())
    {
        if (evt is AgentResponseUpdateEvent e)
        {
            if (e.ExecutorId != lastExecutorId)
            {
                lastExecutorId = e.ExecutorId;
                // ExecutorId looks like "money_coach_d08e4110e9c848eaa0823762ca570c17":
                // the agent name plus an underscore plus a 32-char instance id.
                // Strip the trailing _hex32 for display so the labels stay readable.
                var suffix = e.ExecutorId.LastIndexOf('_');
                var label = suffix > 0 ? e.ExecutorId[..suffix] : e.ExecutorId;
                Console.WriteLine();
                Console.WriteLine($"[{label}]");
            }

            Console.Write(e.Update.Text);
        }
        else if (evt is WorkflowOutputEvent outputEvt)
        {
            newMessages = outputEvt.As<List<ChatMessage>>()!;
            break;
        }
    }

    Console.WriteLine();

    // newMessages is the FULL workflow conversation after this turn, not the delta.
    // Skip the messages we already know about and append only what the workflow added.
    messages.AddRange(newMessages.Skip(messages.Count));
}

return 0;
```

Five things worth reading carefully:

**The conversation lives in a `List<ChatMessage>`, not a session.** Workflows don't take an `AgentSession`. They take the full conversation history every turn. After each turn, the workflow returns the updated message list (its own messages plus whatever the agents produced), and we add the delta back to our running list. The pattern is "send the whole story, receive the whole story plus this turn's chapter".

**`TurnToken(emitEvents: true)` is what kicks the workflow.** Sending the user message isn't enough. The workflow needs an explicit signal that this user turn is ready to execute. `emitEvents: true` says "stream me the agent response updates as they happen" (the alternative is "give me the final output and nothing else").

**`AgentResponseUpdateEvent.ExecutorId` is the agent's name plus an instance suffix.** The raw value looks like `money_coach_d08e4110e9c848eaa0823762ca570c17`: the agent name from Step 2, an underscore, and a 32-character hex instance id MAF generates per workflow run. The REPL code above strips the suffix so the labels stay readable. `LastIndexOf('_')` is the right trim even for `data_analyst`, whose name contains an underscore of its own. The first time the trimmed executor ID changes mid-turn, you've seen a handoff happen live.

**Expect a bare `[money_coach]` with nothing under it.** On a data question the Coach's entire contribution to its first turn is the handoff function call, which carries no text, so the console prints the label and then a blank line before `[data_analyst]` appears. It looks like a bug on a projector and it isn't. Call it out in the room before someone else does, or suppress it: buffer the label and only print it when the first non-empty `e.Update.Text` for that executor arrives.

**`WorkflowOutputEvent` is the "turn done" signal.** When you see it, the workflow has nothing more to say and is waiting for the next user input. Its payload is the full message list the workflow generated this turn. We pull it out via `outputEvt.As<List<ChatMessage>>()!` and use the delta to update our running buffer.

> The `await using StreamingRun run` is a per-turn handle. Each user turn opens one, watches its event stream until `WorkflowOutputEvent`, then disposes. You don't reuse a `StreamingRun` across user turns. The message list is what carries history.

---

## Step 5: Run it

From the repo root:

```bash
dotnet run --project src/FinanceAssistant
```

You should see:

```
Finance assistant (multi-agent). Type a message, or 'exit' to quit.
>
```

Try four prompts. Each shows a different shape of interaction.

**Run each one in a fresh process**: type it, then `exit`, then `dotnet run` again. The four are independent questions, and the shapes described below are what a cold start gives you. In one long session they drift, because the running `List<ChatMessage>` means turn 4 carries turns 1 to 3 into both agents' context. That's worth demonstrating deliberately once, at the end, as the cost of the conversation-broadcast model. It isn't what you want while you're establishing the baseline shapes.

They assume the default `scaffolding/transactions.csv`, which runs from 2024-06-01 to 2026-06-15 with 32 transactions in January 2026 and 25 in February. If you reseeded with different data, adjust the ranges and the topics so the Analyst has something to find. Otherwise the "it lied to me" failure mode is the fixture, not the model.

> One fixture note before you run it live. The seed data is deliberately thin in places. Ask about *coffee* in January 2026 and the honest answer is a single €4.05 espresso, which reads as broken on a projector even though it is correct. "Eating out" is the same question with enough rows behind it to be worth showing.

**1. "How much did I spend eating out in January 2026?"** The Coach receives the question, recognises it needs data, and hands off. The Analyst calls `SearchTransactions` (and usually `GetTransactions` too, to bound the range), totals the matches, and prints its `Findings:` block.

What happens next is a coin toss, so do not announce a three-turn demo before you press enter. **Two shapes are both common.** Either the Analyst stops there and you get two labels, or it hands back and the Coach interprets, giving you three. Reviewers running this on the same deployment have landed 3-of-4 each way on different days. Four labels, three of them empty, turns up occasionally too. Same code, same prompts, same question.

A three-label run:

```
[money_coach]

[data_analyst]
Findings:
- In **2026-01-01 through 2026-01-31**, restaurant-category spending totaled **€129.73** across 7 transactions.
- This includes Pasta & Co (€53.43, €19.23, €24.48), Berlin Cafe (€11.36, €16.71), and The Daily Grind Coffee (€4.05).

I'm handing this to the Money Coach for interpretation.
[money_coach]
You spent **€129.73 eating out in January 2026**, across **7 transactions**.

That averages about **€18.53 per outing**. ...
```

The bare `[money_coach]` at the top is the silent handoff from Step 4, not a bug. The tool names in your trace are `SearchTransactions` and `GetTransactions`: `AIFunctionFactory.Create` takes the method name as-is, so they stay PascalCase rather than being snake_cased for you.

The other face of the coin toss stops one label earlier, on that "I'm handing this to the Money Coach" line, with no third label after it. Read that one closely, because it's the most instructive thing in the demo after the arithmetic: the Analyst wrote the sentence promising a handoff and then never called `handoff_to_1`. The prose and the tool call are two different decisions, and only one of them went your way. Prompt wording moves both, and guarantees neither.

> **Now read that total back against the fixture, out loud, in the room.** January 2026 has **6** restaurant-category rows summing to **€129.26**, and the Analyst's own itemised line lists exactly those six amounts. It then reports 7 transactions and a total that is out. Across repeated runs the reported count has been 6, 7 and 8, and the total has ranged from €119.47 to €134.48, sometimes in dollars. One run itemised three figures summing to €129.77 and then reported €119.47 in the next sentence. The tools return exact rows. The *summing* happens in prose, in the model, and the model is bad at it. This is the single most useful thing on the screen during the demo, and it isn't a multi-agent problem: it's what you get any time you let a model do arithmetic it could have delegated. The fix is a `SumTransactions` tool, not another agent. Say so before someone in the room says it for you.
>
> Two related artefacts of the same "the model is writing the prose" fact: the currency symbol is invented (the seed CSV has an `Amount` column and no currency at all, so a run that comes back in `$` instead of `€` isn't a misconfiguration), and the exact figures won't match this page run to run.

**2. "What's a good rule of thumb for splitting my income between needs, wants, and savings?"** Pure advice question with no data in it at all. The Coach answers from principles (you'll get the 50/30/20 rule and a caveat about it being a guideline) and never hands off. One label, `[money_coach]`. This is where keeping the Coach tool-free pays off: the cheap, fast answer stays on the cheap, fast agent.

> Questions phrased as "should I cut back on X" sit on the boundary and are worth trying if you have time. The Coach may answer from principles, or may hand off for the numbers first, and both are defensible. Pick a topic that exists in the fixture if you try it: "eating out" and "groceries" have rows behind them. Avoid "Uber Eats", which is the worst of both worlds here: there's no such merchant, but there are 19 `Uber` rows in the `Transport` category, so the similarity search returns confidently wrong hits rather than an honest empty result.

**3. "Compare my January and February 2026 spend."** The richest output of the four, and the least predictable. Coach hands off. The Analyst calls `GetTransactions` for both months and reports totals plus a per-category breakdown. The Coach turns it into a comparison table and names the driver (February rent is ~€627 higher, largely offset elsewhere). The substance lands reliably. The *shape* does not: across three runs I got two labels, three labels, and four labels (Coach, Analyst, Coach, Analyst) on the same question.

> Same lesson as prompt 1, now with three data points on one question instead of one. Termination is a model decision. The Analyst's "hand back" paragraph moves the odds, it doesn't settle them, and no amount of prompt wording will. `WithTerminationCondition` from the Step 3 callout is the structural answer when you need the last turn guaranteed. Worth saying out loud, because "add another agent" is the fix people reach for first and it isn't one.

**4. "What was the biggest charge in 2026 so far?"** A pure data question, and the most predictable of the four. Two labels, every run: `[money_coach]` then `[data_analyst]`, ending on the Analyst's `Findings:` block. A fully factual one-line answer terminates the workflow rather than bouncing back, even with the Analyst instructed to always hand back. Contrast it with prompt 3 deliberately: same topology, same prompts, opposite reliability.

If a prompt should clearly involve the Analyst and the Coach answers without handing off, the handoff trigger is too weak. Edit `CoachPrompt.md` to be more explicit about what "needs data" means, and remember from Step 2 that the Analyst's `description:` is what the Coach actually reads when choosing a target. The two prompts in Step 1 are tuned for these four questions. Yours will need iteration.

---

## Step 6: Diff what just happened

Two of the five files are new and untracked, so stage first or git won't count them:

```bash
git add -A src/ && git diff --cached --stat HEAD -- src/
git reset            # put the index back the way you found it
```

```
 src/FinanceAssistant/FinanceAssistant.csproj  |  3 +-
 src/FinanceAssistant/Program.cs               | 78 +++++++++++++++++++++------
 src/FinanceAssistant/Prompts/AnalystPrompt.md | 15 ++++++
 src/FinanceAssistant/Prompts/CoachPrompt.md   | 16 ++++++
 src/FinanceAssistant/Prompts/SystemPrompt.md  | 13 -----
 5 files changed, 96 insertions(+), 29 deletions(-)
```

+96 / -29, and `Program.cs` is most of it: the workflow event-loop body is longer than the `RunAsync` call it replaced. We added one package (`Microsoft.Agents.AI.Workflows`, in the same `1.19.0` line as the base MAF package) and no warning suppression. We didn't rewrite the tools, didn't touch the chat client wiring. The structural change is small. The behavioural change is significant: two agents now share a conversation, with the topology declared in four lines.

The pattern this enables is the one the Pillar 6 forward-look slide was pointing at. Add a third agent (a `BudgetPlanner` that owns multi-month projections, say), give it tools the other two don't have, and extend the `WithHandoffs` block:

```csharp
AgentWorkflowBuilder
    .CreateHandoffBuilderWith(coach)
    .WithHandoffs(coach, [analyst, budgetPlanner])
    .WithHandoffs(analyst, [coach])
    .WithHandoffs(budgetPlanner, [coach])
    .Build();
```

The REPL doesn't change. The event-loop body doesn't change. Only the topology widens.

---

## Stretch goal: an approval gate, and where it moved to

Demo 1 dropped the `ApprovalRequiredAIFunction` confirmation gate from `TransferFundsTool`. An earlier version of this demo put it back here, with a workflow-level recipe built on a `FunctionApprovalRequestEvent`.

**That recipe no longer compiles, and the reason is worth more than the recipe was.**

`FunctionApprovalRequestEvent` doesn't resolve against `Microsoft.Agents.AI.Workflows 1.19.0`. It doesn't appear in the `1.6.1` assemblies either, so the recipe was almost certainly written against a preview surface that never made it to a stable release. Either way the destination is the same: approvals aren't a workflow-orchestration concern any more, they're an agent concern, which means they work whether or not you ever build a workflow.

The pieces, all in `Microsoft.Agents.AI`:

- **`ApprovalRequiredAIFunction`** is unchanged. It's still the `Microsoft.Extensions.AI` wrapper from Pillar 5, and it still only flags a tool as needing approval rather than enforcing anything.
- **`ChatClientAgent` does the enforcing for you.** By default it wraps itself in an internal approval-binding decorator that records every `ToolApprovalRequestContent` the framework surfaces and binds your `ToolApprovalResponseContent` back to the originating tool call on the next turn. You never name that type. The knob you can reach is `ChatClientAgentOptions.DisableApprovalResponseBinding`, which turns the behaviour off if you want to own it yourself.
- **`UseToolApproval(...)` on `AIAgentBuilder`** adds `ToolApprovalAgent` middleware, which queues multiple pending approvals so you can present them one at a time and implements "don't ask again". The public surface for that last part is `ToolApprovalAgentOptions.AutoApprovalRules`, an `IEnumerable<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>>`. The rule type behind it is internal, so predicates are how you express rules.

So the honest version of this stretch goal is: wrap the tool as you did in Pillar 5, put it on whichever agent should own it (the Coach, in this topology), and read `ToolApprovalRequestContent` off the turn before deciding whether to continue. The workflow doesn't need to know.

> **The lesson, which is the actual point of this demo.** This exercise was written against MAF `1.6.1` and is now running on `1.19.0`. Every line of agent and workflow code survived thirteen minors untouched. The one thing that broke was the approval recipe, which was the newest and least settled surface in the framework at the time, and which this guide flagged as such before it broke.
>
> That's the shape of the risk when you build on a young framework. It isn't that everything moves. It's that the newest thing you reached for is the thing most likely to move, and the stable core mostly doesn't. Reach for the young surface with your eyes open, and write down which parts of your code are standing on it.

## Troubleshooting

### Both agents answer every turn

The `WithHandoffs` topology is too permissive or the prompts are too vague. Confirm `CreateHandoffBuilderWith` names exactly one start agent (Coach in this demo). Re-read both prompts. The handoff triggers should be clear paragraphs, not throwaway lines. If the model still picks the wrong agent, raise `ReasoningEffort` off `None` in `AddChatClient` or move to a stronger model: choosing a handoff target is a structured-decision problem and weaker or effort-capped models punt.

### An `[executor]` label prints with nothing under it

Usually not a bug. A label with no text under it means that executor's whole turn was function calls, which carry no text of their own. The bare `[money_coach]` at the top of every data question is exactly this, and so is a bare `[data_analyst]` in a turn where the Analyst only handed back. You'll see runs where three of four labels are empty and the last one carries the whole answer.

It's worth investigating only in one specific shape: `[data_analyst]` empty, and then the *Coach* answering with numbers, with no `Findings:` block anywhere in the turn. That means the Analyst treated the handoff as its entire turn and never reported. Check the "two moves" paragraph made it into `AnalystPrompt.md` verbatim. Models lean on structural cues like a named output block to know what "answering" means before they hand off.

If the empty labels bother you on a projector, buffer the label and print it lazily on the first non-empty `e.Update.Text` for that executor.

### Workflow never emits `WorkflowOutputEvent`

The `TurnToken(emitEvents: true)` is missing or you're disposing the `StreamingRun` before the loop ends. The pattern is: send the token, watch the event stream until you see `WorkflowOutputEvent`, then break and dispose. If you forget to send the token, the workflow accepts the message and waits forever.

### `outputEvt.As<List<ChatMessage>>()` returns null

Not something you should hit on `1.19.0`: the payload really is a `System.Collections.Generic.List<Microsoft.Extensions.AI.ChatMessage>`. Kept here because the wrapper type is the kind of thing that drifts across versions. If a future minor returns something else, inspect `outputEvt.Data` and adjust the cast. The data is always there.

### Labels appear but the names are wrong

Confirm the agent constructions in Step 2 pass `name:`. `ExecutorId` is the agent name plus `_` plus a 32-char hex instance id, so an agent built without a `name` gives you a label that is just the hex tail. Note the trim never yields a blank `[]`: the guard is `suffix > 0`, so an `ExecutorId` with no underscore, or one starting with it, falls through to the raw value.

> **Forward-looking caveat.** In `1.19.0` the streaming event is `AgentResponseUpdateEvent` and the non-streaming sibling is `AgentResponseEvent`, both in `Microsoft.Agents.AI.Workflows`. Both names have held since the early 1.x minors. Microsoft has used `AgentRun*` naming in samples elsewhere, so a future minor may rename one or both. If a future version of MAF stops resolving `AgentResponseUpdateEvent`, dump every `evt.GetType().Name` in the foreach for one turn and pattern-match on whatever the actual streaming type is called. Do not assume a specific replacement name: as of `1.19.0` there's no successor type to guess at.

### Labels look like `[money_coach_d08e4110...]` with a hex tail

That's the raw `ExecutorId` reaching the console. The REPL code in Step 4 trims the trailing `_<hex32>` for display. If your labels still show the suffix, your event-loop body is using `e.ExecutorId` directly instead of the trimmed `label` variable. Compare against the reference block.

### Coach has no tools but still tries to answer data questions itself

The agent's prompt is winning over the topology. Re-read `CoachPrompt.md` and confirm the "hand to the Data Analyst" paragraph is explicit, and check the Analyst's `description:` in Step 2, since that is the text the Coach reads when deciding whether a handoff target fits.

If the model still confabulates numbers, the sampling dial is the next place to look, but note what this repo actually wires. `AddChatClient` in `src/FinanceAssistant/ServiceCollectionExtensions.cs` sets no `Temperature`. It configures `Reasoning` only:

```csharp
.ConfigureOptions(o =>
    o.Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None })
```

The workshop deployment is a reasoning model, which is why that line exists and why adding `o.Temperature = 0.2` next to it may be rejected by the service rather than help. The dial that's actually yours here is `Effort`: raising it off `None` costs latency and tokens and buys better handoff decisions. It's a single enum, and it's the cheapest experiment in this demo.

### The totals don't match the CSV, or the currency changes between runs

Both expected, and both worth showing rather than hiding. The tools return exact rows. The Analyst then sums and formats them in prose, and that arithmetic is the model's, not the database's. The drift is larger than you'd guess: against a ground truth of 6 rows and €129.26, observed runs have reported 6, 7 and 8 transactions and totals from €119.47 to €134.48. One run itemised €97.65 + €28.07 + €4.05 and then reported the total as €119.47, contradicting its own arithmetic in the same message. The currency symbol is invented outright: the seed CSV has an `Amount` column and no currency, so `€` and `$` are both the model guessing.

The structural fix is to stop asking the model to add up. Give the Analyst a tool that returns the total (a `SumTransactions` over the same range predicate `GetTransactions` already uses) and the number stops being a sampling artefact. Note what the fix is not: another agent.

### The two agents disagree on the user's name

Context broadcast in handoff is best-effort. User and agent messages broadcast across agents. Tool-call internals don't. If the agent name comes from a tool call earlier in the turn, the other agent won't see it. The fix is to have the agent that learned the name include it in its answer before handing off, so it lands in a `ChatMessage` the other agent can see.

### `TransferFundsTool` no longer fires

Correct, and expected for this demo. We removed it from the tool surface in Step 2. If you want it back, add it to the Analyst's tool list. The right shape for it in production is on a dedicated agent, wrapped in `ApprovalRequiredAIFunction` as Pillar 5 did, so the transfer never fires without a human in the loop. See the stretch section for where that surface lives now.

---

## You can now

Take a single agent and split it into N specialists with declarative handoff rules. The tools, the chat client, the embedding generator all stay. You author one extra system prompt per specialist and one extra `WithHandoffs` edge per route. The REPL changes once (workflow event loop instead of `RunAsync`) and never again as you add more agents.

The pattern beyond two agents is the same. A triage agent in front of specialists, a back-of-house "escalator" specialist that can refuse and return to triage, sensitive operations behind approval gates on whichever agent owns them (per the stretch section, that gate now lives on the agent, not on the workflow). All of it is the same six API surfaces: `ChatClientAgent` (constructor), `CreateHandoffBuilderWith`, `WithHandoffs`, `RunStreamingAsync`, `TurnToken`, and the workflow events.

The harder questions (when is multi-agent the right call, what evaluation looks like, how the token budget grows, how to keep specialists from collapsing into generalists over time) are real questions and out of scope for the demo. The pattern is what you take home.

---

## Summary

You've changed:

- **`FinanceAssistant.csproj`**: added `Microsoft.Agents.AI.Workflows` (`1.19.0`) and globbed the `Prompts\*.md` copy-to-output entry so the new prompt files reach the build directory. No `<NoWarn>`: the handoff APIs are stable in `1.19.0`.
- **`Program.cs`**: two agent constructions instead of one, a workflow builder, a workflow-driven REPL using `InProcessExecution.RunStreamingAsync` and `TurnToken`. The label-trim for `ExecutorId` keeps the per-agent labels readable.
- **`Prompts/AnalystPrompt.md`** and **`Prompts/CoachPrompt.md`**: two short, specialised system prompts with explicit handoff triggers, a "report in a `Findings:` block, then hand back" paragraph on the Analyst (the `Findings:` half is reliable, the hand-back half moves the odds and nothing more), and an "always interpret after the Analyst returns" paragraph on the Coach that shapes the third turn when it happens.
- **`Prompts/SystemPrompt.md`**: deleted (the old single-agent prompt is no longer used and the glob would otherwise keep copying it). Pillar 5's `tests/FinanceAssistant.Evals` reads that file and is already broken at this point in the workshop for unrelated reasons. It needs rewiring to the split prompts, not the old file back.
- **Tools split**: Analyst owns `GetTransactions`, `SearchTransactions`, `ConvertCurrency`. Coach owns nothing. `TransferFundsTool` is parked.

What survived intact:

- All tool implementations in `Tools/` (besides the parked one).
- `ServiceCollectionExtensions.AddChatClient` and `AddEmbeddingGenerator`.
- The pgvector container, the seed CSV, the embedding backfill.
- The `Microsoft.Agents.AI` package from Demo 1. The workflow primitives ship in a sibling package (`Microsoft.Agents.AI.Workflows`) on the same `1.19.0` release line.

---

## Additional Resources

- [Microsoft Agent Framework Workflows - Handoff orchestration](https://learn.microsoft.com/en-us/agent-framework/workflows/orchestrations/handoff): the canonical handoff documentation, with the math-tutor and history-tutor example this demo's shape is based on.
- [A Tour of Handoff Orchestration Pattern](https://devblogs.microsoft.com/agent-framework/a-tour-of-handoff-orchestration-pattern/): blog walkthrough of the pattern with worked examples.
- [Group Chat Orchestration](https://learn.microsoft.com/en-us/agent-framework/workflows/orchestrations/group-chat): the alternative orchestration when you'd rather let multiple agents speak in a turn-taking loop than hand off explicitly.
- [Microsoft Agent Framework Workflows overview](https://learn.microsoft.com/en-us/agent-framework/workflows/): the wider menu (sequential, concurrent, handoff, group chat, Magentic-One).

The workshop ends here. Demo 1 migrated the single agent to MAF. Demo 2 split it into a multi-agent architecture. The next steps you'll take on your own (checkpointing, durable workflows, evaluation, observability across agents) are on the same MAF surface, split between `Microsoft.Agents.AI` (agents, sessions) and `Microsoft.Agents.AI.Workflows` (orchestration, checkpointing, durable execution). The shape you build first is the shape you keep.
