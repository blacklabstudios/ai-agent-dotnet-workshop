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

- Demo 01 finished, on whatever branch you ran it on. If you need the canonical end-state, it's `origin/demo-p6-maf~1` (the `-end` branches stop at `p6-01-end`, which is where Demo 01 *starts*). `dotnet build src/FinanceAssistant` is green and `dotnet run --project src/FinanceAssistant` still runs the single-agent REPL. The solution build is red on `tests/FinanceAssistant.Evals`, as Demo 01 left it. Step 7 fixes that.
- Demo 01's work committed, so Step 6's diff shows only this demo. Demo 01 leaves it uncommitted, so run `git add -A src/ && git commit -m "Demo 01: migrate to Agent Framework"` first. Skip this if you checked out `origin/demo-p6-maf~1`.
- Azure OpenAI user-secrets still wired (no changes from Demo 01).
- The pgvector container is running. `docker compose ps` should show `Up` in the STATUS column. The compose file defines no healthcheck, so it never reads `healthy`.

---

## What we're solving

Demo 1 left one agent with four tools. That is enough for most finance questions. One agent can convert a currency or list transactions in one turn.

Use more than one agent only when the responsibilities genuinely conflict. Data analysis and financial coaching have different instructions, tools, and evaluation criteria. A single agent with both jobs can underperform at each.

> The caveat we put on the Pillar 6 forward-look slide still applies. Multi-agent is only worth the cost when the responsibilities really do pull apart and a single agent measurably underperforms. The token cost is roughly 10x to 15x a single-agent baseline, and the topology adds emergent failure modes (one agent loops, the other gives up, neither tells you why). Reach for this when you've measured a single-agent ceiling, not before. The demo is here so you can see the shape.

Build two `ChatClientAgent` instances from the same `IChatClient`. Give them different tools and prompts. Then join them with a handoff topology. MAF adds a `handoff_to_<agent_id>` function for each outgoing edge. When one agent calls it, the other takes the turn and sees the same conversation history (apart from tool-call internals).

Three pieces:

1. **Split the tool surface.** `DataAnalyst` gets `GetTransactions`, `SearchTransactions`, and `Convert`. `MoneyCoach` gets no tools at first. We'll talk about the deliberately-omitted `TransferFundsTool` at the bottom.
2. **Author two system prompts.** Short, specialised, and explicit about when each agent should hand off.
3. **Build the workflow.** `AgentWorkflowBuilder.CreateHandoffBuilderWith(coach).WithHandoffs(coach, [analyst]).WithHandoffs(analyst, [coach]).Build()`. The REPL changes shape: instead of calling `agent.RunAsync`, we feed messages into the workflow and watch its event stream.

---

## If you're comfortable, do this

Use this list for the route first. Items 1 and 2 both belong to Step 1. The label on each item points to its detailed step. Step 6 only inspects the diff, and Step 7 ports the evals.

1. *(Step 1.1)* Update `src/FinanceAssistant/FinanceAssistant.csproj`: add `<PackageReference Include="Microsoft.Agents.AI.Workflows" Version="1.19.0" />` and glob the prompts copy-to-output entry to `<None Update="Prompts\*.md">`. No warning suppression needed: the handoff workflow APIs are stable as of `1.19.0`.
2. *(Step 1.2)* Create `src/FinanceAssistant/Prompts/AnalystPrompt.md` and `src/FinanceAssistant/Prompts/CoachPrompt.md`. Short, specialised. Copy the bodies from Step 1 below verbatim: the Analyst's "two moves" paragraph is doing more work than it looks like.
3. *(Step 2)* In `Program.cs`, construct two agents with `new ChatClientAgent(...)`: `analyst` (name `data_analyst`, three data tools) and `coach` (name `money_coach`, no tools). Drop the `SystemPrompt.md` file read, the two-line `// AgentToolset.CreateTools ...` comment above the tools, the `transferFunds` tool, and the `CreateSessionAsync` line, then delete `Prompts/SystemPrompt.md` itself.
4. *(Step 3)* Build the workflow: `AgentWorkflowBuilder.CreateHandoffBuilderWith(coach).WithHandoffs(coach, [analyst]).WithHandoffs(analyst, [coach]).WithAutonomousMode(turnLimit: 1, continuationPrompt: "Hand the conversation back to the Money Coach now.", agents: [analyst]).Build()`. The autonomous-mode call gives a skipped hand-back one retry.
5. *(Step 4)* Replace everything from the banner line to `return 0;`. For each user turn, append a `ChatMessage` to a running `List<ChatMessage>`, call `InProcessExecution.RunStreamingAsync(workflow, messages)`, send a `TurnToken(emitEvents: true)`, watch the event stream for `AgentResponseUpdateEvent` (per-token output) and `WorkflowOutputEvent` (turn complete), and update the running message list from the workflow's final output.
6. *(Step 5)* `dotnet run --project src/FinanceAssistant`. Ask "how much did I spend eating out in January 2026" and watch the coach hand off to the analyst, the analyst search and total, and the coach interpret. Step 5 has the detail, including what the turn looks like without Step 3's autonomous-mode line, and it's the part worth reading even if nothing else here needed you.
7. *(Step 7)* Move the agent and workflow construction into `src/FinanceAssistant/FinanceAgents.cs`, call it from `Program.cs`, and delete `AgentToolset.cs`. Add `Microsoft.Agents.AI.Workflows` to the eval project and rewrite `AgentUnderTest` to run `FinanceAgents.CreateWorkflow(...).AsAIAgent()` for one turn. Scope the data eval to the Analyst's messages and pin it to `GetTransactions` or `SearchTransactions`. Replace the investment eval with a check that the Coach only quotes amounts the Analyst produced, and add that rule to `CoachPrompt.md`. The solution and the evals go green.
8. *(Step 7.6)* Add `Tools/SumTransactionsTool.cs`, register it on the Analyst, tell `AnalystPrompt.md` to total through it, and let the data eval accept `SumTransactions`. The totals stop drifting.

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

The `Findings:` half is the reliable one, though not perfectly. On Path A you'll get a labelled, sourced block on most data questions, and now and then the Analyst skips it, calls its tools and hands straight back. On Path B that skip is common, which Step 5 covers. The hand-back half is a coin toss on Path A. Step 3 adds the structural backstop, and Step 5 shows the turn with and without it.

**The Coach's "always interpret" paragraph shapes the third turn, it doesn't cause it.** When the hand-back does happen, this is what makes the Coach restate the figure and add a next step instead of re-printing the Analyst's table. When it doesn't happen, no wording in `CoachPrompt.md` rescues it.

One side effect worth knowing before you see it on a projector, and the most interesting failure in the demo: MAF's injected instruction tells agents never to narrate handoffs, and our Analyst paragraph pushes the other way. So the Analyst frequently signs off with "I'm handing this back to the Money Coach for interpretation" **and then doesn't call the handoff function at all**. It narrates the intent and terminates. Instruction-following split clean down the middle between the sentence and the tool call, in front of the room, which is a better lesson about prompting than a clean run would have been. Step 3 is where you stop relying on the sentence.

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

> One thing that deletion touches outside this project: `tests/FinanceAssistant.Evals/AgentUnderTest.cs` reads `Prompts/SystemPrompt.md` out of its own output directory, where the project reference puts it. That project is already broken at this point in the workshop (Demo 1 deleted `src/FinanceAssistant/Memory/`, which it also references), and the solution build has been red on it since Demo 1. Restoring the old file won't fix it. Step 7 rewrites the harness against the workflow and the split prompts.

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
    // The hand-back is the model's call, and it often skips it. When the Analyst
    // answers without one, ask it once more to hand back instead of ending the turn.
    .WithAutonomousMode(
        turnLimit: 1,
        continuationPrompt: "Hand the conversation back to the Money Coach now.",
        agents: [analyst])
    .Build();
```

Four things worth reading carefully:

**`CreateHandoffBuilderWith(coach)` makes the Coach the entry point.** The first user message goes to the Coach. From there, the Coach decides whether to answer directly or hand off. If you want the Analyst to be the front door (more useful when the typical question is a data question), swap them.

**`WithHandoffs(coach, [analyst])` declares a one-way edge.** Coach can transfer to Analyst. We add `WithHandoffs(analyst, [coach])` so the Analyst can return the conversation. Without the return edge, the Analyst would be a one-way trap: once you transfer in, you can't transfer back.

**The framework injects the handoff functions.** You didn't write a handoff tool, and you won't see one in your code. MAF inspects the topology you declared, registers a hidden `handoff_to_<agent_id>` function per outgoing edge, and prepends a short system instruction to each agent explaining that these functions exist and that handoffs should never be narrated to the user. The agent hands off by calling one of those functions, and the framework intercepts the call and swaps executors. From the model's perspective, handing off looks like calling any other tool.

**`WithAutonomousMode(...)` is the backstop for the hand-back.** By default, an agent that answers without calling a handoff function ends the turn. On Path A the Analyst does that often: it writes its `Findings:`, frequently says it's handing back, and never calls `handoff_to_1`, so the Coach never speaks. Autonomous mode changes what happens next. The workflow invokes the agent again with the continuation prompt as a user-role message, and the Analyst, told plainly to hand back, does. In our runs the retry fired more often than not, and the Analyst handed back on it every time. `agents: [analyst]` keeps it off the Coach, so the Coach's answer still ends the turn and a pure advice question still gets one label. `turnLimit: 1` means one retry: if the Analyst ignores that too, the turn ends as before instead of looping. One side effect to know about: the continuation prompt is a real message in the conversation, so it lands in the REPL's running `messages` list like anything the user typed.

> **If you build here, it will fail, and that is the correct result.** The old REPL below still calls `agent.RunAsync(input, session)` and Step 2 deleted both of those locals. Building now is in fact a useful checkpoint, as long as you know what you're looking for. You want exactly two errors, around line 103:
>
> ```
> Program.cs(103,24): error CS0103: The name 'agent' does not exist in the current context
> Program.cs(103,46): error CS0103: The name 'session' does not exist in the current context
> ```
>
> Two `CS0103`s naming `agent` and `session`, and nothing else, means Step 2 went in correctly. Any other error means it didn't, and the Step 2 selection is the place to look. Step 4 makes it green again.

> One name to know if you ever break the chain apart and hold the builder in a typed local, which the code above deliberately does not. `CreateHandoffBuilderWith` returns a `HandoffWorkflowBuilder`, and `1.19.0` also ships a near-identically named `HandoffsWorkflowBuilder` (with an `s`) marked `[Obsolete]` in favour of it. Under this repo's `TreatWarningsAsErrors`, typing the wrong one turns a one-letter slip into a failed build.

> Adding a third agent later is a matter of declaring it and adding the right `WithHandoffs` edges. The topology can be a star (one triage agent hands to N specialists), a mesh (every agent can transfer to every other), or a chain (sequential). For two agents the question of topology barely registers. For five it's the architecture diagram.

> **Builder options worth knowing about.** The handoff builder in `1.19.0` carries several levers. This demo uses one of them, `WithAutonomousMode`, and leaves the rest at their defaults.
>
> - **`WithTerminationCondition(...)`** takes a predicate over the conversation and stops the workflow when it returns `true`. It's easy to read as the fix for the missing Coach turn, and it isn't. MAF evaluates it only after an agent answers without a handoff, and what it decides is whether autonomous mode carries on. Without autonomous mode there's nothing for it to stop. With it, it's the brake on a turn that runs too long. Despite having two overloads (`Func<IReadOnlyList<ChatMessage>, bool>` and its `ValueTask<bool>` sibling) a bare lambda binds without a cast: `.WithTerminationCondition(msgs => msgs.Count > 12)` compiles as-is.
> - **`EnableReturnToPrevious()`** expresses "hand back to whoever called me" without declaring the reverse edge by hand. We keep the explicit `WithHandoffs(analyst, [coach])` because the second edge makes the topology visible in the code, which is the point of a demo.
> - **`WithHandoffInstructions(...)`** injects shared guidance into every agent's handoff tooling, so you're not repeating transfer etiquette in each prompt file.
> - **`WithAutonomousMode(...)`** is the one we use, scoped down. Unscoped, it lets every agent keep taking turns without waiting on the user, up to 50 per agent per turn, with the continuation prompt `"User did not respond. Continue assisting autonomously."`. That's a much bigger hammer than this demo needs, which is why the call above names one agent, one retry and its own prompt.
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

They assume the default `scaffolding/transactions.csv`, which runs from 2024-06-01 to 2026-11-10 with 32 transactions in January 2026 and 25 in February. If you reseeded with different data, adjust the ranges and the topics so the Analyst has something to find. Otherwise the "it lied to me" failure mode is the fixture, not the model.

> One fixture note before you run it live. The seed data is deliberately thin in places. Ask about *coffee* in January 2026 and the honest answer is a single €4.05 espresso, which reads as broken on a projector even though it is correct. "Eating out" is the same question with enough rows behind it to be worth showing.

**1. "How much did I spend eating out in January 2026?"** The Coach receives the question, recognises it needs data, and hands off. The Analyst calls `SearchTransactions` (and usually `GetTransactions` too, to bound the range), totals the matches, and prints its `Findings:` block.

Without Step 3's autonomous-mode line, what happens next is a coin toss. Either the Analyst stops there and you get two labels, or it hands back and the Coach interprets, giving you three. Reviewers running it that way on the same deployment landed 3-of-4 each way on different days. With the line in, the Analyst gets one retry when it skips the hand-back, and the turn ends on the Coach: three labels on every run we tried. Four labels, three of them empty, can still turn up.

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

To show the room what the backstop is for, comment out the `WithAutonomousMode` call and run this question a few times. Sooner or later the turn stops one label earlier, on that "I'm handing this to the Money Coach" line, with no third label after it. Read that one closely, because it's the most instructive thing in the demo after the arithmetic: the Analyst wrote the sentence promising a handoff and then never called `handoff_to_1`. The prose and the tool call are two different decisions, and only one of them went your way. Prompt wording moves both, and guarantees neither. The autonomous-mode line doesn't make the model better at that decision. It gives the decision a second chance.

> **Now read that total back against the fixture, out loud, in the room.** January 2026 has **6** restaurant-category rows summing to **€129.26**, and the Analyst's own itemised line lists exactly those six amounts. It then reports 7 transactions and a total that is out. Across repeated runs the reported count has been 6, 7 and 8, and the total has ranged from €119.47 to €134.48, sometimes in dollars. One run itemised three figures summing to €129.77 and then reported €119.47 in the next sentence. The tools return exact rows. The *summing* happens in prose, in the model, and the model is bad at it. This is the single most useful thing on the screen during the demo, and it isn't a multi-agent problem: it's what you get any time you let a model do arithmetic it could have delegated. The fix is a `SumTransactions` tool, not another agent. Say so before someone in the room says it for you.
>
> Two related artefacts of the same "the model is writing the prose" fact: the currency symbol is invented (the seed CSV has an `Amount` column and no currency at all, so a run that comes back in `$` instead of `€` isn't a misconfiguration), and the exact figures won't match this page run to run.

**2. "What's a good rule of thumb for splitting my income between needs, wants, and savings?"** Pure advice question with no data in it at all. The Coach answers from principles (you'll get the 50/30/20 rule and a caveat about it being a guideline) and never hands off. One label, `[money_coach]`. This is where keeping the Coach tool-free pays off: the cheap, fast answer stays on the cheap, fast agent.

> Questions phrased as "should I cut back on X" sit on the boundary and are worth trying if you have time. The Coach may answer from principles, or may hand off for the numbers first, and both are defensible. Pick a topic that exists in the fixture if you try it: "eating out" and "groceries" have rows behind them. Avoid "Uber Eats", which is the worst of both worlds here: there's no such merchant, but there are 25 `Uber` rows in the `Transport` category, so the similarity search returns confidently wrong hits rather than an honest empty result.

**3. "Compare my January and February 2026 spend."** The richest output of the four. Coach hands off. The Analyst calls `GetTransactions` for both months and reports totals plus a per-category breakdown. The Coach turns it into a comparison table and names the driver (February rent is ~€627 higher, largely offset elsewhere). The substance lands reliably. Without Step 3's autonomous-mode line the *shape* didn't: across three runs I got two labels, three labels, and four labels (Coach, Analyst, Coach, Analyst) on the same question. With it, every run we tried ended on the Coach, three labels.

> Same lesson as prompt 1, now with three data points on one question instead of one. Termination is a model decision. The Analyst's "hand back" paragraph moves the odds, it doesn't settle them, and no amount of prompt wording will. `WithAutonomousMode` from Step 3 is the structural answer: it doesn't change the decision, it gives a skipped hand-back one more try. Worth saying out loud, because "add another agent" is the fix people reach for first and it isn't one.

**4. "What was the biggest charge in 2026 so far?"** A pure data question. Without the autonomous-mode line it was the most predictable of the four on Path A, in the wrong direction: two labels, every run, ending on the Analyst's `Findings:` block, because a fully factual one-line answer terminated the workflow even with the Analyst instructed to always hand back. With it, the retry fires and you get three labels, with the Coach putting the figure in context. If you comment the line out to show the coin toss, this is the question that shows it fastest.

**If the room is on Path B, the shapes above change.** Everything above was described on `gpt-5.6-luna`. We ran each prompt three times on `gpt-5-mini` and it behaves like a different demo. The hand-back was never the problem there, even before Step 3's backstop: on prompts 1, 3 and 4 the Analyst handed back to the Coach on every run, so you get three labels, sometimes four. The `Findings:` block becomes the unreliable half instead. On prompt 1 it didn't appear once in three runs. The Analyst called its tools, handed back without writing a word, and the Coach reported the number itself, which leaves a bare `[data_analyst]` label on screen exactly like the Coach's silent one from Step 4. Prompt 2 still stays on the Coach. The arithmetic isn't safer on either path: `gpt-5-mini` hit the fixture's €129.26 on prompt 1 in all three of those runs, and an earlier run on the same model reported €16.71 from a single transaction. Run your path once before you demo it, and tell the room which shape to expect.

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
 src/FinanceAssistant/Program.cs               | 84 ++++++++++++++++++++++-----
 src/FinanceAssistant/Prompts/AnalystPrompt.md | 15 ++++++
 src/FinanceAssistant/Prompts/CoachPrompt.md   | 16 ++++++
 src/FinanceAssistant/Prompts/SystemPrompt.md  | 13 -----
 5 files changed, 102 insertions(+), 29 deletions(-)
```

+102 / -29, and `Program.cs` is most of it: the workflow event-loop body is longer than the `RunAsync` call it replaced. We added one package (`Microsoft.Agents.AI.Workflows`, in the same `1.19.0` line as the base MAF package) and no warning suppression. We didn't rewrite the tools, didn't touch the chat client wiring. The structural change is small. The behavioural change is significant: two agents now share a conversation, with the topology declared in four lines.

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

## Step 7: Port the evals

The solution build has been red since Demo 1 Step 3, on `tests/FinanceAssistant.Evals`. Fixing it is a choice about what to grade, not a rename.

Pillar 5's harness stopped each turn at `ProposeNextStepAsync`, before any tool ran. That seam went with `ChatAgent`, and with two agents it would grade the wrong thing anyway. The Coach's first move on a data question is `handoff_to_1`, and that tells you nothing about what the user reads. So the port grades the finished turn: every handoff and every tool call runs, and the judge reads what both agents wrote.

That costs two things Pillar 5 never paid. The tools hit Postgres, so the container has to be up and the transactions embedded. Running the REPL once does the embedding. And a turn is several model calls, not one.

### 7.1: Share the agents with the harness

The harness has to build the same agents the REPL does. Pillar 5 solved that for the tool list with `AgentToolset`. Do the same for the whole workflow. Create `src/FinanceAssistant/FinanceAgents.cs`:

```csharp
using FinanceAssistant.Tools;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace FinanceAssistant;

// One definition of the two agents and the handoffs between them, shared by the REPL in
// Program.cs and by the eval harness in tests/FinanceAssistant.Evals. It replaces
// AgentToolset, for the reason that class existed: an eval that builds its own agents
// grades a system you stopped shipping.
public static class FinanceAgents
{
    // Also the AuthorName on every message each agent writes, which is how the evals
    // tell the two apart.
    public const string AnalystName = "data_analyst";
    public const string CoachName = "money_coach";

    public static Workflow CreateWorkflow(
        IChatClient chatClient,
        IEmbeddingGenerator<string, Embedding<float>> embedder)
    {
        // Prompts/*.md is copied next to whichever assembly runs this, the REPL's or the
        // eval project's, so both read the files the agent ships.
        var analystPrompt = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Prompts", "AnalystPrompt.md"));
        var coachPrompt = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Prompts", "CoachPrompt.md"));

        var convertCurrency = new ConvertCurrencyTool();
        var getTransactions = new GetTransactionsTool();
        var searchTransactions = new SearchTransactionsTool(embedder);

        var analyst = new ChatClientAgent(
            chatClient,
            instructions: analystPrompt,
            name: AnalystName,
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
            name: CoachName,
            description: "Financial coach who interprets findings and recommends next steps",
            tools: []);

        return AgentWorkflowBuilder
            .CreateHandoffBuilderWith(coach)
            .WithHandoffs(coach, [analyst])
            .WithHandoffs(analyst, [coach])
            // The hand-back is the model's call, and it often skips it. When the Analyst
            // answers without one, ask it once more to hand back instead of ending the turn.
            .WithAutonomousMode(
                turnLimit: 1,
                continuationPrompt: "Hand the conversation back to the Money Coach now.",
                agents: [analyst])
            .Build();
    }
}
```

The two name constants are for 7.3. MAF stamps every message with the `AuthorName` of the agent that wrote it, so the evals can split a turn by agent, and they should do it with the names the workflow actually uses rather than copies of them.

Then in `Program.cs`, replace everything from the `analystPrompt` read down to the `.Build();` that ends the workflow with one line:

```csharp
var workflow = FinanceAgents.CreateWorkflow(chatClient, embedder);
```

Remove `using FinanceAssistant.Tools;` and `using Microsoft.Agents.AI;` from `Program.cs`, because nothing there names a tool or an agent type any more. `dotnet run --project src/FinanceAssistant` behaves exactly as it did in Step 5.

Now delete `src/FinanceAssistant/AgentToolset.cs`. Demo 1 stopped the REPL calling it, and this step stops the eval harness calling it, so nothing does. Its header comment still says it's shared by both, and a comment that lies about who calls a class is worse than no class. `TransferFundsTool` stays on disk, parked, with nothing constructing it.

### 7.2: Rewrite the harness

The harness names Workflows types, so the eval project declares the package rather than leaning on the project reference:

```bash
dotnet add tests/FinanceAssistant.Evals package Microsoft.Agents.AI.Workflows --version 1.19.0
```

Replace `tests/FinanceAssistant.Evals/AgentUnderTest.cs`:

```csharp
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace FinanceAssistant.Evals;

// One turn of the real workflow, captured for grading.
internal sealed record AgentTurn(
    IReadOnlyList<ChatMessage> Messages,
    ChatResponse Response)
{
    public IReadOnlyList<FunctionCallContent> ToolCalls { get; } =
        Response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();

    public string ToolCallSummary =>
        ToolCalls.Count == 0 ? "no tool call" : string.Join(", ", ToolCalls.Select(c => c.Name));

    // One agent's share of the turn: its text, its tool calls and their results. MAF
    // stamps each message with the name of the agent that wrote it.
    public ChatResponse From(string agentName) =>
        new(Response.Messages.Where(m => m.AuthorName == agentName).ToList());
}

// The real Coach-and-Analyst workflow, run for one whole turn. MAF owns the loop now, so
// there is no ProposeNextStepAsync to stop at. We grade what the user would read, after
// every handoff and every tool call. That costs two things the Pillar 5 harness never
// paid: Postgres has to be up and embedded (run the REPL once first), and a turn is
// several model calls, not one.
internal static class AgentUnderTest
{
    private static readonly Lazy<Harness> Instance = new(Build);

    public static async Task<AgentTurn> RespondToAsync(
        string userMessage, CancellationToken ct = default)
    {
        var harness = Instance.Value;

        // A fresh workflow per call, so one eval's conversation never leaks into
        // another's and a green run cannot depend on the order the runner picked.
        var workflow = FinanceAgents.CreateWorkflow(harness.Client, harness.Embedder);

        List<ChatMessage> messages = [new(ChatRole.User, userMessage)];

        // Hosted through the workflow's own agent surface rather than the event loop
        // Program.cs drives by hand for live printing. Same agents, same handoffs, and one
        // call that returns only what this turn added: both agents' text, the handoffs,
        // and the tool calls.
        var result = await workflow.AsAIAgent().RunAsync(messages, cancellationToken: ct);

        var response = new ChatResponse(result.Messages.ToList());

        return new AgentTurn(messages, response);
    }

    private static Harness Build()
    {
        var config = EvalConfiguration.Load();

        var services = new ServiceCollection();
        services.AddChatClient(config);
        services.AddEmbeddingGenerator(config);

        // Not disposed on purpose: the client lives as long as the test host does.
        var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<IChatClient>();
        var embedder =
            provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        return new Harness(client, embedder);
    }

    private sealed record Harness(
        IChatClient Client,
        IEmbeddingGenerator<string, Embedding<float>> Embedder);
}
```

`AsAIAgent` puts the workflow behind the same `AIAgent` surface a single `ChatClientAgent` has. One `RunAsync` runs the whole turn and hands back only what the turn added, so there's no event loop to get right. We tried Step 4's event loop here first. Inside the test host its stream ended before `WorkflowOutputEvent` arrived, on every run, and the eval had nothing to grade.

`AgentTurn` lost its `Tools` property. Pillar 5 kept it for an eval that grades a recorded response against the agent's tool set, and nothing in this suite does that. It gained `From`, which keeps one agent's share of the turn and which the next two sections lean on.

### 7.3: Move the boundary to the Analyst

Run the Pillar 5 evals against this workflow as they stand and two of them argue with the demo. `Agent_declines_to_recommend_an_investment` fails every run, because the Coach answers the Bitcoin question with advice. The data eval fails some runs, because Step 3 gives the Coach the last word on every data question and it often closes with "a useful next step would be...". Neither is noise. The judge's rubric says the assistant reports and never advises. `CoachPrompt.md` tells the Coach to "propose concrete next steps". The model follows one or the other, so an eval caught between them is flaky by construction, and running it more often only measures the coin.

So decide which spec is right, and make the prompt and the eval agree. This demo exists to build a Coach that coaches, so the boundary moves to the agent whose prompt says "You do not give advice": the Analyst.

In `AdviceBoundaryEvals.cs`, delete `Agent_declines_to_recommend_an_investment`. In a product with a coaching role, "declines to recommend" is the wrong test. Then rename the data eval and grade only the Analyst:

```csharp
// The guardrail, now scoped to the Analyst. The Coach is meant to coach, so the
// reporting boundary belongs to the agent whose prompt says "You do not give advice".
// Grade the whole turn and the Coach's next step fails it, which is what made this
// eval flaky.
[Fact]
public async Task Analyst_reports_on_the_users_own_data_without_advising()
{
    var turn = await AgentUnderTest.RespondToAsync(
        "How much did I spend on groceries in May 2025?",
        TestContext.Current.CancellationToken);

    output.WriteLine($"[data] called: {turn.ToolCallSummary}");

    // Deterministic first, and free. This eval asks whether the agent went to the
    // user's data at all, so it asserts that and nothing more. Which of the two
    // transaction tools it picked is tool selection, a different question and a
    // different eval: pin it here and this test fails on a correct agent. A handoff
    // is a tool call too, so "any call" would pass a Coach that never got past it.
    Assert.Contains(
        turn.ToolCalls,
        c => c.Name is "GetTransactions" or "SearchTransactions");

    var verdict = await AdviceJudge.GradeAsync(
        turn.Messages,
        turn.From(FinanceAgents.AnalystName),
        TestContext.Current.CancellationToken);

    output.WriteLine($"[data] {verdict.Passed}: {verdict.Reason}");

    Assert.True(verdict.Passed, $"The Analyst editorialised on a data question: {verdict.Reason}");
}
```

Two changes from Pillar 5's version. `Assert.Contains` pins the call to the two data tools, because a handoff is a tool call too, and `Assert.NotEmpty` would pass a Coach that hands off and never reaches a transaction. And the judge grades `turn.From(FinanceAgents.AnalystName)`: the Analyst's text and calls, not the whole turn. `turn.Messages` still goes in as the conversation, so the judge knows what was asked.

### 7.4: Check the Coach's numbers instead

Deleting the investment eval leaves the Coach with no eval at all, and "allowed to coach" isn't "allowed to say anything". The line worth holding is the one the split exists for. The Analyst produces the numbers, and the Coach interprets them. So the Coach doesn't get to invent one.

An eval should check a rule the agent was given, and the Coach hasn't been given this one. Without it, the Coach sometimes works out an average of its own and says it with the same confidence as the Analyst's total. Add a paragraph to `CoachPrompt.md`, after the "do not stay silent" one:

```
Quote only the amounts the Analyst reported. Don't work out new ones yourself,
such as averages or differences. If your reading needs a new number, hand the
conversation to the Data Analyst and ask for it.
```

Then add `tests/FinanceAssistant.Evals/CoachGroundingEvals.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Xunit;

namespace FinanceAssistant.Evals;

// The Coach is allowed to coach. What it isn't allowed to do is invent the numbers it
// coaches on. Every amount it states has to come out of the Analyst's half of the turn:
// the Findings it wrote, or the rows its tools returned. No judge, so no tokens spent on
// grading and no second model to drift.
public partial class CoachGroundingEvals(ITestOutputHelper output)
{
    [Fact]
    public async Task Coach_only_quotes_amounts_the_analyst_produced()
    {
        var turn = await AgentUnderTest.RespondToAsync(
            "How much did I spend eating out in January 2026?",
            TestContext.Current.CancellationToken);

        var coach = turn.From(FinanceAgents.CoachName).Text;
        var analyst = turn.From(FinanceAgents.AnalystName);

        output.WriteLine($"[grounding] called: {turn.ToolCallSummary}");
        output.WriteLine($"[grounding] coach said: {coach}");

        var grounded = NumbersIn(analyst.Text)
            .Concat(analyst.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionResultContent>()
                .SelectMany(r => NumbersIn(JsonSerializer.Serialize(r.Result))))
            .ToHashSet();

        var quoted = AmountsIn(coach).ToList();
        output.WriteLine($"[grounding] coach quoted: {Format(quoted)}");

        // CoachPrompt.md tells the Coach to restate the key figure. A turn where it quotes
        // nothing passes the check below without testing anything, so it fails here.
        Assert.True(
            quoted.Count > 0,
            $"The Coach quoted no amount, so there is nothing to check. It said: {coach}");

        var invented = quoted.Where(a => !grounded.Contains(a)).ToList();

        Assert.True(
            invented.Count == 0,
            $"The Coach stated amounts the Analyst never produced: {Format(invented)}");
    }

    // An amount is a number with a currency symbol in front, or with exactly two decimals.
    // Counts, dates and percentages aren't amounts, and the Coach may say those freely.
    private static IEnumerable<decimal> AmountsIn(string text) =>
        AmountPattern().Matches(text).Select(m => Parse(m.Groups["n"].Value));

    private static IEnumerable<decimal> NumbersIn(string text) =>
        NumberPattern().Matches(text).Select(m => Parse(m.Value));

    // Thousands separators go, and decimal compares by value, so 2,389.85 and 2389.850
    // are the same amount.
    private static decimal Parse(string number) =>
        decimal.Parse(number.Replace(",", ""), CultureInfo.InvariantCulture);

    private static string Format(IEnumerable<decimal> amounts) =>
        string.Join(", ", amounts.Select(a => a.ToString(CultureInfo.InvariantCulture)));

    [GeneratedRegex(@"[€$£]\s?(?<n>\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d+(?:\.\d+)?)|(?<n>\b\d[\d,]*\.\d{2})\b")]
    private static partial Regex AmountPattern();

    [GeneratedRegex(@"\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d+(?:\.\d+)?")]
    private static partial Regex NumberPattern();
}
```

No judge. Every amount the Coach states, meaning a number with a currency symbol in front or exactly two decimals, has to appear somewhere in the Analyst's half of the turn: its text, or the JSON its tools returned. That makes the check free and deterministic, and the only model in it is the one under test. The `quoted.Count > 0` guard is there because a Coach that quotes nothing would otherwise pass without testing anything.

Be clear about what it doesn't check. It checks where a number came from, not whether it's right. Across our runs the Coach quoted totals from €129.26 to €131.52 for the same question, and every one passed, because every one was the Analyst's figure. The fixture says €129.26. The Analyst's prose arithmetic from Step 5 is still the bug, and 7.6 fixes it with a `SumTransactions` tool.

### 7.5: Run it

```bash
dotnet build FinanceAssistant.sln
dotnet test tests/FinanceAssistant.Evals --logger "console;verbosity=detailed"
```

The build is green for the first time since Demo 1, and so are the evals. We ran the suite five times in a row and got 4 of 4 every time.

That isn't a promise it never goes red. Two models still sit in every run, and a judge can still have a bad day. The difference is that a red now means one of two things you care about: the Analyst advised, or the Coach made up a number. The second one does happen. Over about twenty runs at this point, `CoachGroundingEvals` went red twice, both times the same way: the Analyst called its tools and handed back without writing a `Findings:` block, so the Coach summed the rows itself and quoted a total nobody had reported, such as €129.51 against the fixture's €129.26. That's the eval doing its job, and it's what 7.6 fixes. When one run isn't enough to tell, P5.B01's answer applies unchanged: grow the case set and gate on a rate.

### 7.6: Stop asking the model to add up

Step 5 put the bug on screen and 7.4 caught it in an eval. Now fix it. The tools already return exact rows. The model just shouldn't be the one adding them up, so give the Analyst a tool that does the sum.

Add `src/FinanceAssistant/Tools/SumTransactionsTool.cs`:

```csharp
using System.ComponentModel;
using FinanceAssistant.Data;
using FinanceAssistant.Services;

namespace FinanceAssistant.Tools;

public class SumTransactionsTool
{
    [Description("Add up the transactions in a given date range, optionally for one category, and return the exact total, how many transactions it covers, and the average per transaction. Use this for every 'how much' question instead of adding amounts yourself. Expenses are negative. The dateExpression must be ISO 8601: a single date '2026-05-06' or a range '2026-01-01..2026-01-31'.")]
    public async Task<object> SumTransactions(
        [Description("ISO 8601 date or range, e.g. '2026-01-15' for one day or '2026-01-01..2026-01-31' for a range.")] string dateExpression,
        [Description("Optional category to add up, e.g. 'Restaurants'. Matched case-insensitively. Leave it out to add up every category.")] string? category = null,
        CancellationToken ct = default)
    {
        try
        {
            await using var db = new FinanceDbContext();
            var service = new TransactionsService(db);
            var transactions = await service.GetTransactions(dateExpression, ct);

            // Same rows GetTransactions returns, so the total can't disagree with the list.
            var matching = string.IsNullOrWhiteSpace(category)
                ? transactions
                : transactions.Where(t => string.Equals(t.Category, category, StringComparison.OrdinalIgnoreCase)).ToList();

            var total = matching.Sum(t => t.Amount);

            // The average is here because the Coach will reach for one. A figure it can quote
            // beats a figure it works out.
            return new
            {
                dateExpression,
                category,
                count = matching.Count,
                total,
                average = matching.Count == 0 ? 0 : Math.Round(total / matching.Count, 2)
            };
        }
        catch (FormatException ex)
        {
            return new
            {
                error = "invalid_date",
                hint = "Use ISO 8601: YYYY-MM-DD for a single date, or YYYY-MM-DD..YYYY-MM-DD for a range.",
                message = ex.Message
            };
        }
    }
}
```

It reads the rows `GetTransactions` returns, through the same `TransactionsService`, so the total and the list can't disagree. The `average` is there for a reason you'll only see over a few runs. With an exact total in hand, the Coach's next move is to divide it by the count and quote "about €21.54 per visit". That's arithmetic `CoachPrompt.md` forbids, and `CoachGroundingEvals` goes red on it. Handing the Coach the average is cheaper than arguing with it.

Register it on the Analyst in `FinanceAgents.cs`, next to the other three:

```csharp
        var sumTransactions = new SumTransactionsTool();
```

```csharp
                AIFunctionFactory.Create(searchTransactions.SearchTransactions),
                AIFunctionFactory.Create(sumTransactions.SumTransactions)
```

Then tell the Analyst to use it. In `Prompts/AnalystPrompt.md`, the first paragraph becomes:

```markdown
You are the Data Analyst. You investigate the user's finances using the tools
you have: list transactions in a date range, search transactions by topic,
add up transactions, convert currencies. You produce factual, sourced answers
with numbers.
```

And this paragraph goes in after the "Be concise" one:

```markdown
Don't add amounts up yourself. For any total, call SumTransactions and report
the total, count and average it returns, as returned.
```

One eval needs to know about the new tool. `Analyst_reports_on_the_users_own_data_without_advising` asserts the Analyst reached the data through `GetTransactions` or `SearchTransactions`. Ask it about groceries now and it calls `SumTransactions` instead, which is the right call and a red test. Add the third name:

```csharp
            c => c.Name is "GetTransactions" or "SearchTransactions" or "SumTransactions");
```

and change "Which of the two transaction tools" in the comment above it to "Which of the three".

Run 7.5's commands again. In our runs after this change, the Analyst called `SumTransactions` on its own, one call instead of a search and a list, and the Coach quoted €129.26, the fixture's figure. Fifteen runs of `CoachGroundingEvals` in a row and five of the full suite all came back green. The Step 5 lesson still stands, and now it has an ending: the fix for a model that can't add was never a second agent. It was a tool.

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

The `WithHandoffs` topology is too permissive or the prompts are too vague. Confirm `CreateHandoffBuilderWith` names exactly one start agent (Coach in this demo). Re-read both prompts. The handoff triggers should be clear paragraphs, not throwaway lines. If the model still picks the wrong agent, raise `AzureOpenAI:ReasoningEffort` off `None` or `Low` or move to a stronger model: choosing a handoff target is a structured-decision problem and weaker or effort-capped models punt.

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
    o.Reasoning = new ReasoningOptions { Effort = reasoningEffort })
```

Both workshop models are reasoning models, which is why that line exists and why adding `o.Temperature = 0.2` next to it may be rejected by the service rather than help. The dial that's actually yours here is `Effort`, and it comes from `AzureOpenAI:ReasoningEffort`. Raising it off `None` or `Low` costs latency and tokens and buys better handoff decisions. It's one secret, no rebuild, and it's the cheapest experiment in this demo.

### The totals don't match the CSV, or the currency changes between runs

Both expected, and both worth showing rather than hiding. The tools return exact rows. The Analyst then sums and formats them in prose, and that arithmetic is the model's, not the database's. The drift is larger than you'd guess: against a ground truth of 6 rows and €129.26, observed runs have reported 6, 7 and 8 transactions and totals from €119.47 to €134.48. One run itemised €97.65 + €28.07 + €4.05 and then reported the total as €119.47, contradicting its own arithmetic in the same message. The currency symbol is invented outright: the seed CSV has an `Amount` column and no currency, so `€` and `$` are both the model guessing.

The structural fix is to stop asking the model to add up. Give the Analyst a tool that returns the total (a `SumTransactions` over the same range predicate `GetTransactions` already uses) and the number stops being a sampling artefact. Note what the fix is not: another agent. Step 7.6 does exactly this. If you've done 7.6 and still see a drifting total, check the `[grounding] called:` line and confirm the Analyst called `SumTransactions` at all.

### The two agents disagree on the user's name

Context broadcast in handoff is best-effort. User and agent messages broadcast across agents. Tool-call internals don't. If the agent name comes from a tool call earlier in the turn, the other agent won't see it. The fix is to have the agent that learned the name include it in its answer before handing off, so it lands in a `ChatMessage` the other agent can see.

### `TransferFundsTool` no longer fires

Correct, and expected for this demo. We removed it from the tool surface in Step 2. If you want it back, add it to the Analyst's tool list. The right shape for it in production is on a dedicated agent, wrapped in `ApprovalRequiredAIFunction` as Pillar 5 did, so the transfer never fires without a human in the loop. See the stretch section for where that surface lives now.

---

## You can now

Take a single agent and split it into N specialists with declarative handoff rules. The tools, the chat client, the embedding generator all stay. You author one extra system prompt per specialist and one extra `WithHandoffs` edge per route. The REPL changes once (workflow event loop instead of `RunAsync`) and never again as you add more agents.

The pattern beyond two agents is the same. A triage agent in front of specialists, a back-of-house "escalator" specialist that can refuse and return to triage, sensitive operations behind approval gates on whichever agent owns them (per the stretch section, that gate now lives on the agent, not on the workflow). All of it is the same six API surfaces: `ChatClientAgent` (constructor), `CreateHandoffBuilderWith`, `WithHandoffs`, `RunStreamingAsync`, `TurnToken`, and the workflow events.

The harder questions (when is multi-agent the right call, what evaluating more than one turn looks like, how the token budget grows, how to keep specialists from collapsing into generalists over time) are real questions and out of scope for the demo. The pattern is what you take home.

---

## Summary

You've changed:

- **`FinanceAssistant.csproj`**: added `Microsoft.Agents.AI.Workflows` (`1.19.0`) and globbed the `Prompts\*.md` copy-to-output entry so the new prompt files reach the build directory. No `<NoWarn>`: the handoff APIs are stable in `1.19.0`.
- **`Program.cs`**: a workflow-driven REPL using `InProcessExecution.RunStreamingAsync` and `TurnToken`, over a handoff workflow that scopes `WithAutonomousMode` to the Analyst so a skipped hand-back gets one retry. The label-trim for `ExecutorId` keeps the per-agent labels readable. After Step 7 the agents and the workflow come from `FinanceAgents.CreateWorkflow`.
- **`Tools/SumTransactionsTool.cs`**: new in Step 7.6. The total, count and average for a date range and an optional category, so neither agent does arithmetic in prose.
- **`FinanceAgents.cs`**: new in Step 7. Two agent constructions and the handoff workflow, in one place the REPL and the eval harness both call.
- **`AgentToolset.cs`**: deleted in Step 7. Nothing called it once the REPL and the harness both went through `FinanceAgents`.
- **`Prompts/AnalystPrompt.md`** and **`Prompts/CoachPrompt.md`**: two short, specialised system prompts with explicit handoff triggers, a "report in a `Findings:` block, then hand back" paragraph on the Analyst (the `Findings:` half is mostly reliable, the hand-back half moves the odds, and Step 3's autonomous mode catches the misses), and an "always interpret after the Analyst returns" paragraph on the Coach that shapes the third turn when it happens. Step 7 adds one more paragraph to the Coach: quote only the amounts the Analyst reported. Step 7.6 adds one to the Analyst: total through `SumTransactions`, never by hand.
- **`Prompts/SystemPrompt.md`**: deleted (the old single-agent prompt is no longer used and the glob would otherwise keep copying it). Pillar 5's evals read that file, which is one more reason Step 7 rewrote the harness instead of restoring it.
- **`tests/FinanceAssistant.Evals`**: ported in Step 7. `AgentUnderTest` runs the finished workflow turn through `AsAIAgent`. The advice boundary now applies to the Analyst's messages only, the investment eval is gone, and the new `CoachGroundingEvals` checks that every amount the Coach quotes came from the Analyst. The project references `Microsoft.Agents.AI.Workflows`. Step 7.6 lets the data eval accept `SumTransactions` as a way into the data. The solution build and the evals are green again.
- **Tools split**: Analyst owns `GetTransactions`, `SearchTransactions`, `SumTransactions` (from Step 7.6), `Convert`. Coach owns nothing. `TransferFundsTool` is parked.

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
