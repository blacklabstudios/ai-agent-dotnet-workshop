# P5.01 - Confirmation Gate on Destructive Tools

> Pillar 5, Part 1. Individual.

## Mission

Wire a human-in-the-loop confirmation gate onto destructive tools using `ApprovalRequiredAIFunction` from `Microsoft.Extensions.AI`. By the end, the agent can decide to call a `Transfer` tool, the REPL pauses to show what's about to happen, and the action only goes through after you type "yes". Anything else is treated as a decline and the agent is told the action was not taken.

**Learning Objectives**:

- Mark a tool with `ApprovalRequiredAIFunction`
- Enforce approval in the loop, where tool calls become actions
- Return `user_declined` so the agent can respond without retrying
- Separate a prompt that guides the experience from a gate that enforces it

---

## Prerequisites

- P4.02 finished. `ChatAgent` runs the loop, calls the reducer once per turn, and invokes tools through `AIFunction.InvokeAsync`.
- Postgres running. `Program.cs` opens by calling `EnsureCreatedAsync`, so a stopped container fails this exercise on the first line of Step 4, the first time you run it, not in the part you're about to write. If you closed your laptop since P4.02, `docker compose up -d` from the repo root.

---

## What we're solving

The agent calls a tool when the question looks plausible. That is fine for read-only tools (`GetTransactions`, `SearchTransactions`, `Convert`). It is not fine for a tool that changes the world.

Real systems have irreversible actions: send a payment, send an email, delete a row, cancel a subscription. The model is good at deciding when those actions are *probably* the right call. It's not good enough to be the only thing standing between a wrong inference and your bank account.

Use a human confirmation for destructive tools. Mark the tool. When the agent calls it, pause and show the action. Proceed only after explicit approval. For every other answer, return a structured "user declined" result and let the agent respond.

M.E.AI ships a class for exactly this marking job: `ApprovalRequiredAIFunction`. It's a `DelegatingAIFunction` that wraps another `AIFunction` and signals "I need approval before you call me". Per the framework's own docs, it doesn't enforce the requirement. It's the invoker's responsibility to obtain that approval before invoking. The invoker, in our codebase, is `ChatAgent`'s loop.

We're going to wire that pattern in four pieces:

1. **A destructive demo tool**: `TransferFundsTool.Transfer` is a fake money-transfer tool. It doesn't actually move money. The point is the gate, not the transfer.
2. **A wrapped registration**: in `Program.cs`, the transfer tool's `AIFunction` is wrapped with `new ApprovalRequiredAIFunction(...)` at registration time. The other tools register as plain `AIFunction` and don't need approval.
3. **A type check in `ChatAgent`**: before invoking any tool, the agent asks "is this an `ApprovalRequiredAIFunction`?". If yes, it prompts the user. If they decline, the loop short-circuits with a structured result. The hand-written loop from P3.01 makes this trivial. There's exactly one place where tools get invoked, and the check goes right above it.
4. **One instruction in the system prompt**: telling the model not to ask for confirmation itself. Skip this and the user gets asked twice for one action, once by the model in the chat and once by the gate. Step 4 explains why that happens and what it teaches.

> The confirmation prompt is `Console.WriteLine` and `Console.ReadLine` because that's what the REPL has. In a web app or a Slack bot, the same gate would surface as a button, an "approve / reject" message, or a one-time link. The shape of the prompt is the integration concern. The shape of the check (ask "does this need approval?", ask the human, branch on yes) stays the same.

---

## If you're comfortable, do this

Use this list if you want the route first. The full steps explain the choices and help you recover when a step fails.

1. Create `src/FinanceAssistant/Tools/TransferFundsTool.cs` with one method, `Transfer(string fromAccount, string toAccount, decimal amount, CancellationToken ct = default)`, decorated with `[Description]`. The implementation moves no money and returns a fake success object.
2. Update `ChatAgent`: before invoking any tool, type-check whether it's an `ApprovalRequiredAIFunction`. If yes, prompt the user via the console. Short-circuit with a `user_declined` result on anything other than "yes".
3. In `Program.cs`, register the new tool wrapped with `new ApprovalRequiredAIFunction(...)`. Read-only tools register as plain `AIFunction` and don't trigger the gate.
4. Add one instruction at the bottom of `src/FinanceAssistant/Prompts/SystemPrompt.md` telling the model never to ask for confirmation itself. Without it you get two prompts for one action.
5. Run (Postgres up). Ask the agent to "Transfer 100 from Checking to Savings". The gate fires on iteration 1. Type `yes` once, `no` once, and verify both paths. Only the word `yes` proceeds, case-insensitive. `y` does not.
6. Ask for a read-only tool and a transfer in one sentence ("convert 100 EUR to USD, and transfer 100 from Checking to Savings"). Watch `Convert` run ungated in the same batch that stops on `Transfer`.

---

## Step 1: Create TransferFundsTool.cs

Create `src/FinanceAssistant/Tools/TransferFundsTool.cs`:

```csharp
using System.ComponentModel;

namespace FinanceAssistant.Tools;

public class TransferFundsTool
{
    [Description("Transfer money between user accounts. This action is irreversible and the user will be asked to confirm before it runs.")]
    public Task<object> Transfer(
        [Description("Source account name, e.g. 'Checking'")] string fromAccount,
        [Description("Destination account name, e.g. 'Savings'")] string toAccount,
        [Description("Amount to transfer in account currency. Must be positive.")] decimal amount,
        CancellationToken ct = default)
    {
        // No real transfer. Nothing here moves money or writes a log line.
        // We just return a fake success. The point of this tool is the gate, not the transfer.
        return Task.FromResult<object>(new
        {
            transferred = amount,
            from = fromAccount,
            to = toAccount,
            transactionId = Guid.NewGuid()
        });
    }
}
```

Two things worth noticing:

**No `[RequiresUserConfirmation]` attribute on the method.** The "needs approval" signal lives at registration time (the wrap), not on the method itself. That's a deliberate framework choice. The same tool method can be wrapped or not depending on how the agent wants to govern it.

**The description tells the model the action is irreversible.** That nudges the model to be careful about *when* it calls the tool. The confirmation gate is the second line of defence: even if the model decides to call, the human gets the final say.

> The return type is `Task<object>` rather than a typed DTO. We want the JSON shape the model will see, and the anonymous object is the shortest way to express it. A real codebase would use a `TransferResult` record. The shape that hits the model is the same either way.

---

## Step 2: Update ChatAgent to check for ApprovalRequiredAIFunction

Two edits in `src/FinanceAssistant/ChatAgent.cs`. No new `using` directives: the file already imports `Microsoft.Extensions.AI`, which is where `ApprovalRequiredAIFunction` lives. While we're touching the tool-invocation loop, the existing nesting (`if function is null / else { try / catch }`) gets one level deeper with the new approval check. We flatten it with early continues so the three branches (unknown tool, declined approval, invoke) read as peers instead of as nested boxes.

### 2.1: Replace the tool-call loop body

Find the existing `foreach (var call in toolCalls)` block inside `RunTurnAsync` and replace the whole block with this flat version:

```csharp
            foreach (var call in toolCalls)
            {
                var function = _options.Tools?
                    .OfType<AIFunction>()
                    .FirstOrDefault(f => f.Name == call.Name);

                if (function is null)
                {
                    _store.AppendToolResult(new FunctionResultContent(
                        call.CallId,
                        $"Tool '{call.Name}' is not registered."));
                    continue;
                }

                if (function is ApprovalRequiredAIFunction && !ConfirmInteractive(call))
                {
                    _store.AppendToolResult(new FunctionResultContent(
                        call.CallId,
                        new
                        {
                            error = "user_declined",
                            message = "The user did not confirm the action. Do not retry without explicit user permission."
                        }));
                    continue;
                }

                AIContent resultContent;
                try
                {
                    var result = await function.InvokeAsync(new AIFunctionArguments(call.Arguments), ct);
                    resultContent = new FunctionResultContent(call.CallId, result);
                }
                catch (Exception ex)
                {
                    // Tool threw something the model didn't handle (or we didn't wrap at the tool boundary).
                    // Hand the model a structured error so it can recover or apologise.
                    resultContent = new FunctionResultContent(call.CallId, $"Tool error: {ex.Message}");
                }

                _store.AppendToolResult(resultContent);
            }
```

### 2.2: Add the ConfirmInteractive helper

At the bottom of the `ChatAgent` class, add a private static method that owns the prompt UX. Keeping it out of the loop body is what lets the loop stay flat:

```csharp
    private static bool ConfirmInteractive(FunctionCallContent call)
    {
        var argsPretty = call.Arguments is { Count: > 0 }
            ? string.Join(", ", call.Arguments.Select(kv => $"{kv.Key}={kv.Value}"))
            : "(no arguments)";

        Console.WriteLine($"[agent] '{call.Name}' requires confirmation.");
        Console.WriteLine($"        Arguments: {argsPretty}");
        Console.Write("        Type 'yes' to proceed: ");
        var answer = Console.ReadLine()?.Trim();
        return string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);
    }
```

Six things worth reading carefully:

**Three top-level branches, not nested boxes.** The loop body now reads as: "unknown tool → log and skip", "needs approval and the user declined → log and skip", "otherwise → invoke and record". Each branch ends with `continue` or falls through to the invocation block. Adding a fourth gate later (rate limit, audit log, per-user quota) is one more early-`continue` block above the `try`, not another nested `if` inside an `else`.

**The check is `function is ApprovalRequiredAIFunction`.** No new field on `ChatAgent`, no set to maintain, no reflection. The framework's wrapper type carries the signal, and a single `is` check picks it up. When you wrap a function with `new ApprovalRequiredAIFunction(inner)`, the resolved function's runtime type is `ApprovalRequiredAIFunction`, so the pattern match fires.

**The prompt shows the arguments.** Without that, "Transfer requires confirmation" tells the user nothing about what's being approved. With it, the user sees `fromAccount=Checking, toAccount=Savings, amount=100` and can make an informed decision. Showing the arguments is the difference between a real confirmation and rubber-stamping whatever the model produced. The helper method (`ConfirmInteractive`) owns this formatting so the loop body doesn't have to.

**Only `yes` proceeds, and `y` does not.** The comparison is `string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase)`, so `Yes` and `YES` are fine and `y`, `yeah` and `sure` are all declines. Expect to type `y` out of habit at least once and watch the agent tell you the transfer was declined. That's strict on purpose. A gate that accepts near-misses is a gate that gets cleared by a stray keystroke, and the point of the exercise is that the last thing standing in front of a real transfer should be deliberate. If you'd rather be kinder about it, widen the match. Just make the decision on purpose rather than by accident.

**`ConfirmInteractive` is where `ct` stops.** Every other call in the loop takes the `CancellationToken` and honours it. `Console.ReadLine()` can't. It blocks the thread until the user presses enter, and no token will pull it out. That's fine for a REPL, where the human waiting on stdin *is* the process. It's the first thing to change on any other surface, and `p6-b01` changes it, by ending the turn at the gated call and resuming it when the answer arrives on a later request.

**The declined result is structured, not an exception.** The agent appends a `FunctionResultContent` carrying `{ error = "user_declined", ... }` and continues the loop. The model sees the result on the next iteration and can apologise, ask the user what they'd prefer, or move on. Throwing an exception here would surface as a generic tool error and the model would probably retry. The `"Do not retry without explicit user permission"` hint in the message tells it not to bother.

> Wrapping with `ApprovalRequiredAIFunction` does NOT change how the function is invoked. The wrapper is a `DelegatingAIFunction`. When you call `wrapper.InvokeAsync(...)`, it forwards to the inner function. The check we just added decides *whether* to invoke. The framework deliberately separates "mark it" from "enforce it" so different invokers (a console REPL, a web app, a background worker) can implement the approval UX their way.

> **M.E.AI ships one of those invokers, and we deleted it in P3.01.** If you had kept `UseFunctionInvocation()`, `FunctionInvokingChatClient` would spot the wrapper and refuse to auto-invoke it. It replaces the `FunctionCallContent` with a `ToolApprovalRequestContent`, hands that back out to you, and waits for a matching `ToolApprovalResponseContent` on a later request before it will run the tool. Same idea, more machinery, because it has to work for callers who can't block on a `Console.ReadLine`. It also behaves differently in one way worth knowing: if any call in a response needs approval, **every** call in that response needs approval, including the read-only ones. Our check is per call. That's a small dividend from having written the loop by hand.

> The prompt uses the same `Console.ReadLine()` the REPL itself uses, so the gate and the REPL are both pulling from one stdin. Type your answers by hand. If you pipe them in, a line meant for one prompt can be swallowed by the other, and the result looks like a working decline rather than a mistake. Troubleshooting has the symptom and the tell.

---

## Step 3: Wrap the destructive tool in Program.cs

Find the existing `chatOptions` block:

```csharp
var convertCurrency = new ConvertCurrencyTool();
var getTransactions = new GetTransactionsTool();
var searchTransactions = new SearchTransactionsTool(embedder);

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

Add `TransferFundsTool` to the instances, and wrap its registration with `ApprovalRequiredAIFunction`:

```csharp
var convertCurrency = new ConvertCurrencyTool();
var getTransactions = new GetTransactionsTool();
var searchTransactions = new SearchTransactionsTool(embedder);
var transferFunds = new TransferFundsTool();

var chatOptions = new ChatOptions
{
    Tools =
    [
        AIFunctionFactory.Create(convertCurrency.Convert),
        AIFunctionFactory.Create(getTransactions.GetTransactions),
        AIFunctionFactory.Create(searchTransactions.SearchTransactions),
        new ApprovalRequiredAIFunction(AIFunctionFactory.Create(transferFunds.Transfer))
    ]
};
```

That last line is the entire opt-in. Wrap the function to mark it. Don't wrap to leave it un-gated. The other three tools remain plain `AIFunction` instances and the type check in `ChatAgent` sees them as `not ApprovalRequiredAIFunction` and lets them through.

The diff is three lines in one block: the `transferFunds` instance alongside the other three, a trailing comma on the previous last entry (`SearchTransactions)` → `SearchTransactions),`), and the new wrapped registration on the line after it. Auto-format won't add the comma for you.

No new `using` directives here either. `Program.cs` already imports both `Microsoft.Extensions.AI` and `FinanceAssistant.Tools`.

`ChatAgent`'s constructor doesn't change. The check we added in Step 2 reads from `_options.Tools`, which the agent already has. The `chatAgent = new ChatAgent(...)` line from P4.02 stays exactly as it was.

> Compared to a per-tool attribute or a hand-rolled `IReadOnlySet<string>` of names, this is the cleanest version of the pattern. The marker is at the registration site, where the decision actually lives. The agent doesn't need to know which tools were marked. It only needs to recognise the marker type at invocation time.

---

## Step 4: Tell the model not to ask for confirmation itself

Everything's wired. Run it now, with Postgres up:

```bash
dotnet run --project src/FinanceAssistant
```

The gate works. It also asks you to approve the same transfer twice:

```
> Transfer 100 from Checking to Savings.
[memory] 1 messages in history
[agent] iteration 1: final answer
I can't initiate the transfer until you confirm. Transfer $100 from Checking to Savings?
> yes, please do it
[memory] 3 messages in history
[agent] iteration 1: calling Transfer
[agent] 'Transfer' requires confirmation.
        Arguments: fromAccount=Checking, toAccount=Savings, amount=100
        Type 'yes' to proceed: yes
[agent] iteration 2: final answer
Transferred $100 from Checking to Savings successfully.
```

You approved that transfer twice. Once in the chat, once at the gate. Only the second one was yours.

Look at the first log line: `iteration 1: final answer`, not `iteration 1: calling Transfer`. The model didn't call anything. It read a tool description that says "irreversible", decided that deserved a question, and asked. Your gate had nothing to fire on, because nothing was about to run.

> **If your run went straight to `iteration 1: calling Transfer`, you haven't missed a step.** Whether the model hedges first is a judgment it makes fresh every time. Mine did it on most runs and skipped it on others, with the same sentence typed in. Ask again in a new REPL and you'll probably see it. Add the line below either way. That the model can't be relied on to hedge is the same reason it can't be relied on not to, and one prompt per action shouldn't depend on which way a run lands.

It's a polite question, and it's useless. The model asks whether to proceed, you say yes, and then the real gate asks the same thing. Two prompts for one action. That's how people learn to treat approvals as a formality they click through.

Open `src/FinanceAssistant/Prompts/SystemPrompt.md` and add this at the bottom:

```
Never ask the user to confirm before calling a tool. The application enforces
approval separately, and asking first only duplicates that prompt.
```

That's the whole fix. The file is copied to the output directory on build, so `dotnet run` picks it up with no extra step. It has to be a new `dotnet run`, though. The REPL you started at the top of this step is still holding the old prompt in memory, and it read it once at startup. Type `exit` and leave it stopped. Step 5 starts clean.

Now the description and the system prompt split the work. The description still says the action is irreversible, which keeps the model careful about *when* to call. The system prompt says whose job the confirmation is. Not the model's.

**Here's the part worth sitting with.** You just changed the model's behaviour with two sentences of English, and it worked. It'll keep working right up until it doesn't, because a prompt is a request. Swap the model, raise the temperature, or let a user say "actually, always double-check with me first", and the hedge can come back. None of that touches your gate. The gate is a type check in a loop you wrote, and it fires whether the model is careful, careless, or actively trying to route around you.

The prompt made the experience nicer. The gate is what makes it safe. Only one of those is a guarantee.

---

## Step 5: Run, approve, decline

Postgres has to be up. If you closed your laptop since P4.02, run `docker compose up -d` first. The REPL from Step 4 has to be stopped, so the new system prompt gets read and the conversation starts empty.

Then, from the repo root:

```bash
dotnet run --project src/FinanceAssistant
```

Try the approval path:

```
> Transfer 100 from Checking to Savings.
[memory] 1 messages in history
[agent] iteration 1: calling Transfer
[agent] 'Transfer' requires confirmation.
        Arguments: fromAccount=Checking, toAccount=Savings, amount=100
        Type 'yes' to proceed: yes
[agent] iteration 2: final answer
Transferred $100 from Checking to Savings.
```

One prompt, and it's yours. The model went straight to the tool call on the opening message (`iteration 1: calling Transfer`, no hedging sentence in front of it) and the gate caught it before anything ran. That's Step 4 doing its job.

> The `[memory]` counts are one run, not a target. They move with how many turns the conversation takes.

> **Want to see a tool signature fail in public?** Ask for "100 **EUR** from Checking to Savings" instead. `Transfer` has no currency parameter, so the currency has nowhere to go. What the model does on the way there varies. Mine called `Convert` first, turned the 100 EUR into 110.00 USD, and then transferred 100 anyway. Yours might skip the conversion entirely. Either way the gate prints `Arguments: fromAccount=Checking, toAccount=Savings, amount=100` and the currency is nowhere in it. The model didn't refuse, and it didn't ask. It filled the parameters it had and dropped what wouldn't fit, and a detour through `Convert` makes that look more like handling the problem than it is. That's P2.01's lesson arriving uninvited, and the gate is what makes it visible, because you get to read the arguments *before* the tool runs. Worth doing once, deliberately, which is why it isn't in the prompt above.

Your exact wording will vary. The model paraphrases the structured return value (`transactionId`, `transferred`, `from`, `to`) into plain English, so you might get a transaction ID quoted back, or just a sentence. What matters is that the prompt fired, the answer was "yes", and the tool actually ran.

Now the decline path. Stay in the same REPL and ask for another transfer:

```
> Transfer 50 from Savings to Checking.
[memory] 5 messages in history
[agent] iteration 1: calling Transfer
[agent] 'Transfer' requires confirmation.
        Arguments: fromAccount=Savings, toAccount=Checking, amount=50
        Type 'yes' to proceed: no
[agent] iteration 2: final answer
The transfer was not completed because it was declined.
```

Two things to notice about the decline path:

1. **The agent didn't retry the transfer with different arguments.** The `"Do not retry without explicit user permission"` hint in the structured error did its job. It went straight to a final answer on the next iteration.
2. **The agent recovered in plain English, and we wrote no code for that.** The model read `{ error = "user_declined" }` as a result rather than a crash, and wrote a sentence about it. How warm that sentence is depends on the model. You might get the terse version above, or an offer to try different accounts. Either one is the pattern working.

Staying in the same REPL is deliberate. The decline then lands with a *successful* transfer already sitting in the conversation, which is the harder case and the more realistic one. It also lets you watch the `[memory]` counter from P4.02 climb across both turns. Keep going and you'll see the reducer fold the transfer turns into a summary without breaking the pairing between a tool call and its result.

Finally, ask the agent something unrelated like "What did I spend on coffee?" and confirm the gate doesn't trip. Read-only tools should fire without any prompt, because their `AIFunction` instances are not wrapped in `ApprovalRequiredAIFunction`.

---

## Step 6: Force a mixed batch and watch the gate go per call

Step 5 proved the gate stops a transfer. It didn't prove the more interesting half: that it stops **only** the transfer. A model can ask for several tools in a single response, and the note back in Step 2 claimed our per-call check is a dividend from having written the loop by hand. Time to collect it.

Stay in the same REPL again and ask for a read-only tool and a destructive one in the same breath:

```
> Do two things in one go: convert 100 EUR to USD, and transfer 100 from Checking to Savings. Both now.
[memory] 13 messages in history
[memory] reducing 9 messages into 1 summary
[agent] iteration 1: calling Convert, Transfer
[agent] 'Transfer' requires confirmation.
        Arguments: fromAccount=Checking, toAccount=Savings, amount=100
        Type 'yes' to proceed: yes
[agent] iteration 2: final answer
- Converted 100 EUR = 110.00 USD
- Transferred 100 from Checking to Savings successfully.
```

Ignore the second line for a moment. Three turns of Step 5 pushed the history past the reducer's threshold of 12, so P4.02 folded the head of the conversation into a summary on the way in. That's the payoff Step 5 promised you. The history it just rewrote holds a successful transfer, a declined one, and both sets of tool results, and every call still sits next to the result that answers it. Your counts will differ from mine. The reducer firing somewhere around here won't.

Now look at `iteration 1: calling Convert, Transfer`. Two calls, one response, one `foreach`. `Convert` was resolved, found not to be an `ApprovalRequiredAIFunction`, and invoked without a word to you. `Transfer` hit the same `is` check one loop iteration later and stopped. One prompt, for the one call that earned it.

Now ask for the same two things again, in the same REPL, and type `no` at the gate this time. The conversion still lands. Only the transfer comes back as `user_declined`, and the model reports one success and one refusal in the same answer.

That's the whole argument from the Step 2 note made visible. `FunctionInvokingChatClient` would have escalated the entire response: because *any* call needed approval, *every* call would have been replaced with a `ToolApprovalRequestContent`, and you would be approving a currency conversion to get at the transfer. Approving things that don't need approving is how people learn to approve without reading. Our gate asks about exactly one thing.

> Getting the model to batch two calls in one response is the only fiddly part. If it splits them across two turns you'll see `calling Convert` and `calling Transfer` on separate iterations, which still works but doesn't show the point. Phrasing that helps: "in one go", "both now", "at the same time". If your model refuses to batch, take the result on faith. The check sits inside the per-call `foreach`, so it can't behave any other way.

---

## Troubleshooting

### Prompt fires for every tool call

You wrapped more than the destructive tool. Look at your `chatOptions.Tools` list. Only `transferFunds.Transfer` should be wrapped with `new ApprovalRequiredAIFunction(...)`. The other three (Convert, GetTransactions, SearchTransactions) should be plain `AIFunctionFactory.Create(...)`.

### Prompt never fires for Transfer

Three things to check in order:

1. The `chatOptions.Tools` list actually wraps `AIFunctionFactory.Create(transferFunds.Transfer)` inside `new ApprovalRequiredAIFunction(...)`. If you just added the unwrapped function, the gate doesn't trigger.
2. The type check inside `ChatAgent` reads `function is ApprovalRequiredAIFunction`. If you used a different type name or missed the using for `Microsoft.Extensions.AI`, the check won't compile or won't match.
3. The model is actually picking the tool. Check the `[agent] iteration 1: calling Transfer` log line. If the model is calling a different tool (or no tool), the gate has nothing to fire on.

### The gate declines even though you typed `yes`

You approved, and the agent still reported the transfer as declined.

Nine times out of ten this means you piped input in rather than typing it. The gate and the REPL read the same stdin, so a line meant for one can be swallowed by the other. If the model asks a clarifying question you didn't plan for, every line after it shifts up by one and the gate eats the line you wrote for the REPL. It's a convincing failure, because a swallowed `exit` isn't the exact word `yes`, so the gate correctly declines and the agent correctly apologises. Everything downstream of the mistake behaves perfectly.

The tell is in the transcript: count the `>` prompts against the lines you sent. If they don't line up, that's it. Type the answers by hand and it goes away.

### The model still asks before the gate does

You added the line in Step 4 and you're still getting two prompts. Check that you saved `src/FinanceAssistant/Prompts/SystemPrompt.md` and rebuilt. The file is copied to the output directory with `PreserveNewest`, so an unsaved edit or a stale process keeps serving the old prompt.

If the wording is in place and the model still hedges, that's the Step 4 lesson landing the hard way rather than a bug. Strengthen the sentence, or accept it and note that the gate held either way. The one thing that would be a real bug is the gate failing to fire.

### Agent retries after a decline

The structured error message wasn't strong enough. Two escalations, in order:

1. Strengthen the hint on the declined result: `"The user has declined this action. Do not call this tool again in this conversation without the user explicitly asking for it."`. This is enough for most models.
2. If the model still hammers the tool, you have two settings on `ChatOptions` that act as harder mitigations. `Temperature = 0` makes the model more deterministic and less prone to creative retries. `ToolMode = ChatToolMode.None` forbids any tool call on the turn entirely, which is the hard escape hatch when a particular model decides to be persistent. Either can be applied for just the next turn, then reset.

### Prompt shows `Arguments: (no arguments)`

The formatter is fine. `call.Arguments` really is null or empty, which means the model issued the tool call without filling in `fromAccount`, `toAccount` or `amount`. That's a tool-description problem rather than a gate problem, and it's the one case where the gate is doing you a real favour: it shows you an empty argument list *before* anything runs.

Fix it where the model gets its information. Check that every parameter on `TransferFundsTool.Transfer` still carries its `[Description]`, and that the method description still says what the tool does. This is P2.01's "break the descriptions on purpose" Extra seen from the other end. If the descriptions are intact and it still happens, put the request in a more explicit sentence ("transfer 100 from Checking to Savings") and see whether the arguments come back.

### `ApprovalRequiredAIFunction` is not found

It's in `Microsoft.Extensions.AI` (specifically the `Microsoft.Extensions.AI.Abstractions` assembly that ships transitively with the main package). Both `Program.cs` and `ChatAgent.cs` already import it, which is why Steps 2 and 3 said no new `using` was needed. If you see this error, you've put the code in a file that doesn't. Add `using Microsoft.Extensions.AI;` at the top of that one.

---

## You can now

Mark any tool as destructive by wrapping its `AIFunction` with `new ApprovalRequiredAIFunction(...)` at registration time. The agent will pause before calling it, show the user what's about to happen, and only proceed on explicit approval. Decline is treated as a structured signal the agent can recover from, not an exception.

The gate lives inside the agent loop, in one place, applied uniformly. Adding a tenth destructive tool later is one wrap in `Program.cs`. No new code paths in `ChatAgent`.

You also know it works per call, not per response. When the model asks for a read and a write together, only the write gets a prompt. That keeps approvals rare enough that people still read them.

And you know where the prompt ends and the guarantee begins. One sentence in the system prompt stopped the model asking a question it had no business asking. The gate is the part that would still hold if that sentence stopped working.

---

## Summary

You've added:

- **`Tools/TransferFundsTool.cs`**: a fake destructive tool with `[Description]` only. The framework's wrapper, not a per-method attribute, marks it as needing approval.
- **`ChatAgent` updated**: before each tool invocation, type-checks `function is ApprovalRequiredAIFunction`. On match, prompts the user via the console and short-circuits with a `user_declined` structured result on anything other than "yes".
- **`Program.cs` updated**: the destructive tool's `AIFunction` is wrapped with `new ApprovalRequiredAIFunction(...)` at registration. Read-only tools register as plain `AIFunction`. `ChatAgent`'s constructor signature is unchanged.
- **`Prompts/SystemPrompt.md` updated**: one instruction telling the model not to ask for confirmation itself, so the user gets one prompt per action instead of two. The prompt improves the experience. The gate is what enforces it.
- **A working confirmation gate**: tried both approve and decline paths, watched the agent recover gracefully from the decline.
- **Per-call granularity, demonstrated**: forced the model to batch a read-only call with a destructive one and watched `Convert` run ungated while `Transfer` stopped for approval.

---

## What's next

P5.02 is the other half of Pillar 5. The gate stops the agent doing something irreversible without a human. Evaluations put a number on whether its answers are any good, so a quality regression fails the build like a broken unit test instead of getting noticed by a user. The judge turns out to be an `IChatClient`, the same abstraction you wired in P1.02.

---

## Additional Resources

- [Microsoft.Extensions.AI: `IChatClient` and tool calling](https://learn.microsoft.com/en-us/dotnet/ai/ichatclient)
- [`ApprovalRequiredAIFunction` API reference](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.approvalrequiredaifunction)
- [Anthropic: building effective agents (on human oversight of consequential steps)](https://www.anthropic.com/engineering/building-effective-agents)
