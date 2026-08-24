# P6.B01 - From Console REPL to a Web Chat

> Pillar 6 bonus. Optional. Pick up after P5.01.

## Mission

Put the agent in a browser through AG-UI, an open protocol for streaming between an agent and a front end. The new ASP.NET Core project serves a chat page. Each tab gets its own conversation, and the P5.01 confirmation gate becomes Approve and Decline buttons.

Run `dotnet run --project src/FinanceAssistant.Web`, then open `http://localhost:5080`. The console REPL still works. `src/FinanceAssistant/Program.cs` does not change.

One caveat up front, because it's better to know now than in Step 6. Whether you actually see the answer arrive word by word is decided by your Azure deployment, not by the code in this guide. Deployments that filter content synchronously buffer the stream and release it in one batch, and the workshop deployment is one of them. You are building the streaming path either way, and Step 6 shows you how to tell the difference between a buffered service and a bug.

**Learning Objectives**:

- Stream an agent loop that can call tools
- Use AG-UI as the wire format
- Model human approval as an interrupt and a resume
- Give each browser conversation its own `ConversationStore`

---

## Prerequisites

- P5.01 finished. `ChatAgent` owns the loop, calls the reducer once per turn, invokes tools through `AIFunction.InvokeAsync`, and gates `TransferFundsTool` behind `ApprovalRequiredAIFunction`.
- The Postgres + pgvector container is running. From the repo root: `docker compose up -d`.
- The four `AzureOpenAI:*` user secrets from P1.01 and P2.02 are set against the `finance-assistant-workshop` UserSecretsId.
- Two new NuGet packages, `AGUI.Server` and `AGUI.Abstractions`. The second is redundant on purpose. Step 1 says why, and why the version number should worry you slightly.

> **If you stopped earlier than P5.01.** P4.01 is the real floor. Step 2.2 rewrites the loop from P3.01, but every line of it reads and writes a `ConversationStore`, and that type doesn't exist until P4.01. Below that, this exercise has nothing to stand on.
>
> **If you skipped P4.02**, `SummarizingHistoryReducer` doesn't exist, and it's named in two places. In Step 2.2 remove the `_reducer` field, the `SummarizingHistoryReducer? reducer = null` parameter, the assignment, and both `if (_reducer is not null)` blocks. In Step 3 remove the `reducer:` argument. Keep `using FinanceAssistant.Memory;` in both files. `ConversationStore` lives in that same namespace, and dropping the using breaks `new ConversationStore()`.
>
> **If you skipped P5.01**, the approval path comes out whole. In 2.2 remove the `decisions` block and its `else`, keeping only the body of the `else`, and remove the `gated` list along with the `if (function is ApprovalRequiredAIFunction)` branch and the `if (gated.Count > 0)` block that follows the loop. Invoke every call directly. In 2.3 remove the `ToolApprovalRequestContent` case, the `pending` variable, the `while (true)` wrapper and `ConfirmInteractive`. In Step 5 remove `renderApproval` and the `RUN_FINISHED` branch that calls it. `CloseDanglingCalls` stays either way, because a cancelled request can orphan a call without any approval being involved.
>
> **If you went further than P5.01.** P5.02 added `ChatAgent.ProposeNextStepAsync`, and the eval harness in `tests/FinanceAssistant.Evals` is the only caller. Step 2.2 replaces that whole file, so the method is kept in the listing on purpose. Do not delete it. That test project is not in `FinanceAssistant.sln`, so `dotnet build` at the repo root builds green while the evals no longer compile. Step 2 ends with the command that catches it.

---

## What we're solving

Several people asked to see the agent in a browser. Fair. A console REPL is good for learning an agent loop. It is not how you show someone the product.

The browser is not only a new front end. It exposes assumptions the REPL hid.

Pillar 6 called the MCP server "a new front door over the same building". This is another front door. Same tools, same loop, same store, same system prompt. But MCP is a machine-facing door, and machines don't mind waiting eleven seconds in silence, don't have a back button, and don't open two tabs. A human-facing door asks three questions the console never had to answer:

1. **Can you stream?** The console printed the whole answer at once and nobody minded, because a terminal that pauses looks like a terminal that is working. A web page that pauses looks broken. P1.02 showed you `GetStreamingResponseAsync` and then said, honestly, that streaming *with tools in the loop* is a bigger job than it looks. This is that job.

2. **Whose conversation is this?** There's exactly one `ConversationStore` in the console app because there's exactly one user, one process, one terminal. Open the web app in two browsers and that assumption dies. Conversation identity was always a real design question. The console just answered it for you by accident.

3. **Who is standing at the keyboard?** `ConfirmInteractive` reads from `Console.ReadLine`. On a web server there's no console session to read from, and even if there were, it belongs to the operator, not to the person who asked for the transfer. P5.01 said the shape of the prompt is an integration concern and the shape of the check stays the same. Time to collect on that.

There's a fourth question, and it's the one that decides how much code you write today. **What goes on the wire?** Every answer to the first three has to be expressed as bytes a browser can read. You could invent that format. A record with a `kind` field, a `text` field, a `toolName` field, and a page that switches on `kind`. It works, it takes about forty lines, and it's wrong in a way that only shows up later: nothing else in the world can read it. Your second client, your teammate's React app, an off-the-shelf chat UI, all of them have to be taught your private vocabulary first.

AG-UI is that vocabulary, already written down. It's an open protocol for exactly this problem, it runs over Server-Sent Events, and it has names for the things your loop already does: text arriving in pieces, a tool starting, a tool returning, a run pausing for human approval. Speaking it means the endpoint you build today renders in CopilotKit or the AG-UI Dojo without you writing an adapter for either.

---

## Where the console leaked into your agent

Before writing any web code, it's worth naming what has to move and what doesn't. Four things in `ChatAgent` are console-shaped, and only four:

| What | Where it is now | What it becomes |
| --- | --- | --- |
| `Console.WriteLine("[agent] iteration N: calling X")` | Inside the loop | An update the caller renders however it likes |
| `Console.WriteLine("[agent] iteration N: final answer")` | Inside the loop | Same |
| `Console.Error.WriteLine("[agent] iteration cap ...")` | After the loop | Same |
| `ConfirmInteractive` calling `Console.ReadLine` | A private static method | A pause the caller resolves whenever it can |

Everything else stays. The loop and its cap, the `FinishReason` check, the `FunctionCallContent` scan, the `CallId` pairing, the `user_declined` payload, the reducer call, the store. Those were never about the console. They are the agent.

There's a fifth leak, and it's one file over. `SummarizingHistoryReducer` prints `[memory] reducing N messages into 1 summary` straight to `Console`, so the web server's terminal keeps that one line even after `ChatAgent` goes quiet. We're leaving it. It's a good reminder that "console-shaped" is a property of a codebase, not of a class, and that you find the last one by running the thing and reading the output.

That's the whole refactor in one sentence: **the loop stops printing and starts yielding, and it stops asking and starts pausing.** Two changes, and a second front end becomes possible. This is what "the loop is yours now" from P3.01 was worth.

---

## If you're comfortable, do this

Use this list if you want the route first. The full steps explain the choices and help you recover when a step fails.

1. `dotnet new web -o src/FinanceAssistant.Web`, add it to the solution, add a `ProjectReference` to `FinanceAssistant`, the shared `UserSecretsId`, and `AGUI.Server` plus `AGUI.Abstractions`, both at version `0.0.6`.
2. In `FinanceAssistant`, rewrite `ChatAgent` so the loop is an iterator yielding `ChatResponseUpdate`. Gated calls leave as `ToolApprovalRequestContent` and end the turn. A turn that arrives carrying `ToolApprovalResponseContent` resumes instead of starting. Keep `ProposeNextStepAsync`, because the P5.02 evals call it. `Program.cs` in the console app does not change.
3. In the web project, add `ChatSession` (one store, one agent, one turn at a time) and `SessionRegistry`, keyed on the AG-UI thread id.
4. Write the web `Program.cs`: reuse `AddChatClient` and `AddEmbeddingGenerator`, run the same database and embedding startup block, configure the AG-UI JSON resolvers, then map one `POST /api/chat` that converts a `RunAgentInput` and returns `TypedResults.ServerSentEvents`.
5. Write `wwwroot/index.html`, `app.css`, and `app.js`. The JavaScript reads AG-UI frames off a `fetch` body and switches on `event.type`.
6. Run both projects. Ask a question, watch a tool fire, ask for a transfer, approve it, then decline one.

---

## Step 1: Create the web project

From the repo root:

```bash
dotnet new web -o src/FinanceAssistant.Web
```

```bash
dotnet sln add src/FinanceAssistant.Web/FinanceAssistant.Web.csproj
```

Replace `src/FinanceAssistant.Web/FinanceAssistant.Web.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <RootNamespace>FinanceAssistant.Web</RootNamespace>
    <UserSecretsId>finance-assistant-workshop</UserSecretsId>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="AGUI.Abstractions" Version="0.0.6" />
    <PackageReference Include="AGUI.Server" Version="0.0.6" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\FinanceAssistant\FinanceAssistant.csproj" />
  </ItemGroup>

</Project>
```

Four things worth reading carefully:

**Two packages, and one of them is redundant on purpose.** `AGUI.Server` already brings `AGUI.Abstractions` transitively. It's listed anyway because your code opens with `using AGUI.Abstractions;`, and a direct using deserves a direct reference. Everything web-shaped (minimal APIs, static files, Server-Sent Events) is still in the ASP.NET Core shared framework that `Microsoft.NET.Sdk.Web` gives you, so those two lines are the whole dependency story.

**`0.0.6` is a real version number and you should read it as one.** The AG-UI .NET SDK was published on 27 August 2026 and its public surface still sits in `PublicAPI.Unshipped.txt` upstream, which is the maintainers saying out loud that nothing is frozen yet. Pin the exact version, as above. If a later version breaks the build, the three names to check are `RunAgentInput.ToChatRequestContext`, `ChatRequestContext`, and the `AsAGUIEventStreamAsync` extension, because those three are the entire API this exercise uses. The protocol itself is more stable than the SDK, so the JavaScript in Step 5 is the part least likely to move.

There's a second .NET route to AG-UI, `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore`, and it's one line of code instead of the two files you're about to write. It wraps a Microsoft Agent Framework `AIAgent`. We are not using it, because MAF replaces the hand-written loop with its own, and the loop is the thing this workshop spent Pillar 3 building. Demo 1 does that migration deliberately. Today the loop stays yours and AG-UI wraps it.

**Same `UserSecretsId` as the agent and the MCP server.** All three projects read the same secret store. You don't re-run `dotnet user-secrets set`. Note that `WebApplicationBuilder` only loads user secrets when the environment is Development, which is what `dotnet run` gives you by default. Deploy this and the secrets have to come from somewhere else.

**`Prompts/SystemPrompt.md` and `scaffolding/transactions.csv` come along for free.** Both are `None` items with `CopyToOutputDirectory` in `FinanceAssistant.csproj`, and the SDK flows those through a `ProjectReference` into the referencing project's output. So `AppContext.BaseDirectory` resolves them in the web app exactly the way it does in the console app, with no csproj edits. There's nothing to check until the web project has been built once. The guide's first build is at the end of Step 2, and after it `src/FinanceAssistant.Web/bin/Debug/net10.0/` should contain both.

`dotnet new web` also leaves you a `Properties/launchSettings.json` with a random port and `"launchBrowser": true` in it. Ignore both. Step 4 pins the address in code and explains why the startup log names the file anyway.

---

## Step 2: Give the agent an event stream instead of a console

This is the only step that touches the `FinanceAssistant` project, and it is the step that matters. One rewritten file. No new ones.

### 2.1: The type you aren't going to write

Stop for a second before the code, because the most useful thing in this step is a file that doesn't get created.

The obvious move here is a `record AgentEvent` with a `Kind` enum covering tokens, tool calls, approval requests and the final answer. It's maybe forty lines, it serialises cleanly, and a page can switch on it. Plenty of production agents ship exactly that.

We aren't writing it, for two reasons.

The first is that .NET already has the type. `ChatResponseUpdate` is what `GetStreamingResponseAsync` hands you, it carries a `Contents` list, and every state this loop is ever in has a content type waiting for it: `TextContent` for a token, `FunctionCallContent` for a tool call, `FunctionResultContent` for its result, and `ToolApprovalRequestContent` for a gated call. Those are all `Microsoft.Extensions.AI` types you already have. A parallel hierarchy would carry the same information with different names.

The second is that the wire format was never ours to choose. AG-UI names these events already, in a spec other people implement:

| What the loop produces | What AG-UI emits |
| --- | --- |
| `TextContent` | `TEXT_MESSAGE_START`, then `TEXT_MESSAGE_CONTENT` per fragment, then `TEXT_MESSAGE_END` |
| `FunctionCallContent` | `TOOL_CALL_START`, `TOOL_CALL_ARGS`, `TOOL_CALL_END` |
| `FunctionResultContent` | `TOOL_CALL_RESULT` |
| `ToolApprovalRequestContent` | `TOOL_CALL_START`, `TOOL_CALL_ARGS`, `TOOL_CALL_END`, then `RUN_FINISHED` carrying an interrupt outcome |

Read that last row twice. One update carrying one `ToolApprovalRequestContent` becomes four events, because a client that is about to draw an approval card needs to know which tool and which arguments it is approving. The interrupt itself names only an id. Step 2.2 leans on this and Step 5's `calls` Map exists because of it.

The mapping is a package, not an exercise. `AGUI.Server` ships one extension method, `AsAGUIEventStreamAsync`, that takes your `IAsyncEnumerable<ChatResponseUpdate>` and returns `IAsyncEnumerable<BaseEvent>`. It also emits `RUN_STARTED` and the terminal `RUN_FINISHED` for you.

So the whole of Step 2 is: make the loop yield `ChatResponseUpdate` instead of printing, and put the right content in it. That's the file below.

### 2.2: The loop, rewritten

Replace `src/FinanceAssistant/ChatAgent.cs` entirely:

```csharp
using System.Runtime.CompilerServices;
using System.Text;
using FinanceAssistant.Memory;
using Microsoft.Extensions.AI;

namespace FinanceAssistant;

public class ChatAgent
{
    // Agent bookkeeping the protocol has no field for: which iteration produced an update, and
    // how the turn ended. It rides in AdditionalProperties because the AG-UI adapter maps
    // Contents and turns nothing here into an event, so no client has to know it exists.
    // It is still visible in the rawEvent echo the adapter attaches to every event, which is
    // a debugging convenience with no opt-out in 0.0.6. Do not put anything private here.
    internal const string IterationKey = "agent.iteration";
    internal const string OutcomeKey = "agent.outcome";
    internal const string OutcomeFinal = "final";
    internal const string OutcomeCapped = "capped";

    private readonly IChatClient _chatClient;
    private readonly ChatOptions _options;
    private readonly ConversationStore _store;
    private readonly SummarizingHistoryReducer? _reducer;
    private readonly int _maxIterations;

    // The cap counts model calls per user turn. An approval pause splits a turn across two
    // requests, so this survives the pause and only resets when a new user message arrives.
    private int _iteration;

    public ChatAgent(
        IChatClient chatClient,
        ChatOptions options,
        ConversationStore store,
        string systemPrompt,
        SummarizingHistoryReducer? reducer = null,
        int maxIterations = 8)
    {
        _chatClient = chatClient;
        _options = options;
        _store = store;
        _reducer = reducer;
        _maxIterations = maxIterations;

        _store.AppendSystemMessage(systemPrompt);
    }

    // P5.02's eval seam. One model call against the conversation as this agent assembles it,
    // stopping before any tool runs. RunTurnStreamingAsync no longer routes through it, so this
    // is not the literal first call a turn makes any more. It still builds the request the same
    // way: same store, same reducer, same options. What an eval grades here is what the agent
    // would send.
    public async Task<ChatResponse> ProposeNextStepAsync(string input, CancellationToken ct = default)
    {
        _store.AppendUserMessage(input);

        if (_reducer is not null)
        {
            await _reducer.TryReduceAsync(_store, ct);
        }

        return await _chatClient.GetResponseAsync(_store.Messages, _options, ct);
    }

    public async Task<string> RunTurnAsync(string input, CancellationToken ct = default)
    {
        var answer = new StringBuilder();
        IList<ChatMessage> incoming = [new ChatMessage(ChatRole.User, input)];

        while (true)
        {
            ToolApprovalRequestContent? pending = null;

            await foreach (var update in RunTurnStreamingAsync(incoming, ct))
            {
                var iteration = IterationOf(update);

                if (OutcomeOf(update) == OutcomeFinal)
                {
                    Console.WriteLine($"[agent] iteration {iteration}: final answer");
                }
                else if (OutcomeOf(update) == OutcomeCapped)
                {
                    Console.Error.WriteLine(
                        $"[agent] iteration cap of {iteration} hit. The agent looped on tool calls without producing a final answer.");
                }

                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent text:
                            answer.Append(text.Text);
                            break;

                        case FunctionCallContent call:
                            // Only the text after the last tool call is the answer. Chatter the
                            // model wrote before calling a tool is not.
                            answer.Clear();
                            Console.WriteLine($"[agent] iteration {iteration}: calling {call.Name}");
                            break;

                        case ToolApprovalRequestContent request:
                            answer.Clear();
                            if (request.ToolCall is FunctionCallContent gated)
                            {
                                Console.WriteLine($"[agent] iteration {iteration}: calling {gated.Name}");
                            }
                            pending = request;
                            break;
                    }
                }
            }

            if (pending is null)
            {
                break;
            }

            // The web front end ends the HTTP response here and waits for a second request.
            // The console has a human already blocked on stdin, so it answers and re-enters.
            var approved = pending.ToolCall is FunctionCallContent gatedCall && ConfirmInteractive(gatedCall);
            incoming = [new ChatMessage(ChatRole.User, [pending.CreateResponse(approved)])];
        }

        return answer.Length > 0 ? answer.ToString() : "(no final answer, iteration cap reached)";
    }

    public async IAsyncEnumerable<ChatResponseUpdate> RunTurnStreamingAsync(
        IList<ChatMessage> incoming,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // An approval decision arrives as its own turn carrying no new user text. That is the
        // whole difference between resuming a paused turn and starting a new one.
        var decisions = incoming
            .SelectMany(m => m.Contents)
            .OfType<ToolApprovalResponseContent>()
            .ToList();

        if (decisions.Count > 0)
        {
            foreach (var decision in decisions)
            {
                if (decision.ToolCall is not FunctionCallContent call)
                {
                    continue;
                }

                var result = decision.Approved
                    ? await InvokeAsync(Find(call.Name), call, ct)
                    : Declined(call.CallId);

                _store.AppendToolResult(result);
                yield return Update(ChatRole.Tool, result);
            }

            // A batch can hold more than one gated call. Anything the client did not answer
            // is declined here, so the assistant message leaves with every call resolved.
            CloseDanglingCalls();
        }
        else
        {
            // Order matters. An abandoned approval left an assistant message holding a call
            // with no result, and a tool result only pairs with the call directly above it.
            // Close it before the new user message lands in between and breaks the pair.
            CloseDanglingCalls();

            _iteration = 0;
            _store.AppendResponseMessages(incoming.Where(m => m.Role != ChatRole.System));

            if (_reducer is not null)
            {
                await _reducer.TryReduceAsync(_store, ct);
            }
        }

        while (_iteration < _maxIterations)
        {
            _iteration++;

            List<ChatResponseUpdate> updates = [];

            await foreach (var update in _chatClient.GetStreamingResponseAsync(_store.Messages, _options, ct))
            {
                updates.Add(update);

                // Text goes out as it arrives. Tool-call fragments do not: until the call is
                // complete we cannot tell whether it is gated, and a gated call leaves as an
                // approval request rather than as a call.
                if (!string.IsNullOrEmpty(update.Text))
                {
                    yield return new ChatResponseUpdate(update.Role ?? ChatRole.Assistant, update.Text)
                    {
                        MessageId = update.MessageId,
                        ResponseId = update.ResponseId,
                        AdditionalProperties = Bookkeeping()
                    };
                }
            }

            var response = updates.ToChatResponse();
            _store.AppendResponseMessages(response.Messages);

            if (response.FinishReason != ChatFinishReason.ToolCalls)
            {
                yield return Outcome(OutcomeFinal);
                yield break;
            }

            var toolCalls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .ToList();

            List<FunctionCallContent> gated = [];

            foreach (var call in toolCalls)
            {
                var function = Find(call.Name);

                if (function is ApprovalRequiredAIFunction)
                {
                    // Run every ungated call first. Pausing with a sibling unanswered would
                    // leave the store holding a call with no result.
                    gated.Add(call);
                    continue;
                }

                yield return Update(ChatRole.Assistant, call);

                var result = await InvokeAsync(function, call, ct);
                _store.AppendToolResult(result);
                yield return Update(ChatRole.Tool, result);
            }

            if (gated.Count > 0)
            {
                foreach (var call in gated)
                {
                    yield return Update(ChatRole.Assistant, new ToolApprovalRequestContent(call.CallId, call));
                }

                // The turn stops here. A resume finishes it.
                yield break;
            }
        }

        yield return Outcome(OutcomeCapped);
    }

    private AIFunction? Find(string name) =>
        _options.Tools?.OfType<AIFunction>().FirstOrDefault(f => f.Name == name);

    // ConversationStore's invariant: every tool call is followed by its result. Any exit path
    // that skips the result breaks the next turn rather than this one, and those are the
    // expensive bugs, because the stack trace points at the wrong request.
    private void CloseDanglingCalls()
    {
        var contents = _store.Messages.SelectMany(m => m.Contents).ToList();

        var answered = contents.OfType<FunctionResultContent>().Select(r => r.CallId).ToHashSet();

        var orphans = contents
            .OfType<FunctionCallContent>()
            .Select(c => c.CallId)
            .Where(id => !answered.Contains(id))
            .Distinct()
            .ToList();

        foreach (var orphan in orphans)
        {
            _store.AppendToolResult(Declined(orphan));
        }
    }

    private static async Task<AIContent> InvokeAsync(AIFunction? function, FunctionCallContent call, CancellationToken ct)
    {
        if (function is null)
        {
            return new FunctionResultContent(call.CallId, $"Tool '{call.Name}' is not registered.");
        }

        try
        {
            var result = await function.InvokeAsync(new AIFunctionArguments(call.Arguments), ct);
            return new FunctionResultContent(call.CallId, result);
        }
        catch (Exception ex)
        {
            // Tool threw something the model didn't handle (or we didn't wrap at the tool boundary).
            // Hand the model a structured error so it can recover or apologise.
            return new FunctionResultContent(call.CallId, $"Tool error: {ex.Message}");
        }
    }

    private static AIContent Declined(string callId) =>
        new FunctionResultContent(
            callId,
            new
            {
                error = "user_declined",
                message = "The user did not confirm the action. Do not retry without explicit user permission."
            });

    private static bool ConfirmInteractive(FunctionCallContent call)
    {
        Console.WriteLine($"[agent] '{call.Name}' requires confirmation.");
        Console.WriteLine($"        Arguments: {FormatArguments(call)}");
        Console.Write("        Type 'yes' to proceed: ");
        var answer = Console.ReadLine()?.Trim();
        return string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatArguments(FunctionCallContent call) =>
        call.Arguments is { Count: > 0 } arguments
            ? string.Join(", ", arguments.Select(kv => $"{kv.Key}={kv.Value}"))
            : "(no arguments)";

    private ChatResponseUpdate Update(ChatRole role, AIContent content) =>
        new(role, [content]) { AdditionalProperties = Bookkeeping() };

    private ChatResponseUpdate Outcome(string outcome) =>
        new(ChatRole.Assistant, []) { AdditionalProperties = Bookkeeping(outcome) };

    private AdditionalPropertiesDictionary Bookkeeping(string? outcome = null)
    {
        var properties = new AdditionalPropertiesDictionary { [IterationKey] = _iteration };

        if (outcome is not null)
        {
            properties[OutcomeKey] = outcome;
        }

        return properties;
    }

    private static int IterationOf(ChatResponseUpdate update) =>
        update.AdditionalProperties?.TryGetValue(IterationKey, out var value) == true && value is int iteration
            ? iteration
            : 0;

    private static string? OutcomeOf(ChatResponseUpdate update) =>
        update.AdditionalProperties?.TryGetValue(OutcomeKey, out var value) == true ? value as string : null;
}
```

That's a long listing, so here's what actually changed, in the order it will bite you.

**`RunTurnStreamingAsync` takes messages, not a string, and that's the approval design.** One door, two kinds of turn. A new question arrives as a user message. A decision on a pending approval arrives as a `ToolApprovalResponseContent`, with no user text at all. The `decisions` list at the top is the whole test, and everything else follows from it.

**The resume carries the arguments, and this loop runs them.** Look at what `decision.ToolCall` actually is. It was rebuilt from the payload the client sent back, not read out of the store, so `InvokeAsync` executes the client's arguments rather than the ones the model proposed. Approving means "run this call", not "run the call you were shown". Send *any* `CallId` back with `amount` changed to 999999 and the transfer runs for 999999. The id isn't checked against anything the server issued, so it isn't the validated half. Nothing is. That doesn't make it harmless, and Step 6 shows what a wrong id costs. Try it once in Step 6 and you won't forget it.

For a demo that's acceptable, because the only client is the page in Step 5 and it echoes back exactly what the server just sent it. For anything real it isn't. Look the call up by `CallId` in the store, invoke that one, and treat the payload's `arguments` as a display hint. A lookup that finds nothing has to be an error, not a fall-through, or you've moved the hole rather than closed it. It's the same trust rule as the thread id in Step 3, arriving from the other side: a field the client controls isn't evidence about the client.

**`GetStreamingResponseAsync` plus `ToChatResponse()`, exactly as P1.02 promised.** The fragments go into a list *and* the text ones go out to the caller as they arrive. When the stream ends, `updates.ToChatResponse()` collapses the list back into the `ChatResponse` the non-streaming call would have handed you, `Messages` and `FinishReason` included. Everything below that line is P3.01's loop. Streaming turned out to be a rendering choice after all, and the seam was one method call.

**Only text passes through live, and that's deliberate.** The old shape of this loop yielded every fragment. This one rebuilds a text-only update and drops the rest. The reason is the gate: a tool-call fragment arrives before the call is complete, and until it's complete you can't look up the function and discover it needs approval. Announce the call early and a gated tool gets announced twice, because the AG-UI adapter emits `TOOL_CALL_START` a second time when it sees the approval request. Waiting until `ToChatResponse()` costs you streamed tool arguments, which nobody has ever wanted to watch arrive character by character, and buys you one announcement per call.

**Gated calls are collected, not handled in place.** The `gated` list looks like an optimisation and isn't. If the model asks for two tools and the first is gated, pausing immediately leaves the second call with no result sitting in the store, which is the failure the next point is about. Running every ungated call first means the pause happens with exactly the gated ones outstanding, and the resume can close all of them.

**`CloseDanglingCalls` is the most important twenty lines in this file.** `ConversationStore` has an invariant, which is that every tool call is followed by its result. The interrupt model breaks it by design: the turn ends with an assistant message holding `tool_calls` and no results, on purpose, because a human is being asked something. That's fine while the user is deciding. It isn't fine if the user never decides, closes the tab, and comes back tomorrow with a different question, because the bad pair sits in the history and every later turn resends it. The API rejects the whole conversation:

```
System.ClientModel.ClientResultException: HTTP 400 (invalid_request_error: )
Parameter: messages.[5].role

An assistant message with 'tool_calls' must be followed by tool messages responding to each
'tool_call_id'. The following tool_call_ids did not have response messages: call_gz0AGV6YlNHLyiQLrgVx4x1l
```

Note where the fix sits in the `else` branch: *before* `AppendResponseMessages`, not after. A tool result only pairs with the call directly above it, so appending the new user message first and the orphan result second produces the same 400 you were trying to avoid. Getting that order wrong is a five-minute bug with a twenty-minute stack trace, and Step 6 has you reproduce it on purpose.

**The iteration counter is a field now, and it survives the pause.** P5.01 printed `iteration 1: calling Transfer` and then `iteration 2: final answer`. A transfer is now two HTTP requests, so a counter local to the method would restart and print `iteration 1` twice. `_iteration` resets only when a new user message arrives, which keeps the console output identical and makes the cap mean what it says: eight model calls per user turn, however many requests that turn takes.

**`AdditionalProperties` carries what the protocol has no field for.** AG-UI has no concept of "iteration 3 of the agent's internal loop", and it shouldn't. That number is ours, so it rides in `AdditionalProperties`, which the adapter maps to no event at all. The console reads it back through `IterationOf`. Be aware that the adapter attaches a `rawEvent` echo of the whole update to every event it emits, so this is visible on the wire even though nothing reads it. Do not put anything private there.

**`InvokeAsync` is a separate method for a boring compiler reason.** `yield return` can't appear inside a `try` block that has a `catch`. The tool-invocation `try`/`catch` doesn't yield, so lifting it out is enough to keep the iterator legal without changing behaviour.

### 2.3: What the console does with all this

`RunTurnAsync` is still there and `src/FinanceAssistant/Program.cs` still needs no edit at all. It's now a fold over the updates, and it grew an outer `while (true)` for one reason: when the loop pauses, somebody has to answer.

That's the sentence worth taking away from this exercise. **The console doesn't have a special approval path. It has the same interrupt the browser gets, answered faster.** `ConfirmInteractive` prompts on stdin, the answer becomes a `ToolApprovalResponseContent`, and the loop is re-entered with it. The browser does the identical thing across two HTTP requests and a pair of buttons. One mechanism, two speeds.

Two small things in that output did change, and neither is in `Program.cs`. P5.01 joined the tool names and printed one line per iteration, so two tools in one model turn read as `calling SearchTransactions, GetTransactions`. This version prints one `[agent]` line per call, because it is folding a stream of updates rather than reading a finished list. And on the iteration cap it returns the fixed `(no final answer, iteration cap reached)` string, where P5.01 fell back to the last assistant text in the store. Both are invisible on the happy path. Neither is worth restoring.

This is also what P5.01's `IApprovalGate` would have been, if we had built one. We didn't, and it's worth knowing what that saved. A gate interface needs a web implementation, and a web implementation needs a `TaskCompletionSource` the loop parks on, a timeout for the tab that closes, a second endpoint to resolve it, and a rule for what happens when an approval arrives for a call nobody is waiting on. All of that exists to keep one HTTP request open across a human decision. The interrupt doesn't keep it open, so none of it needs to exist.

Build the solution:

```bash
dotnet build
```

Now build the eval project on its own. It isn't in `FinanceAssistant.sln`, so the command above never touched it:

```bash
dotnet build tests/FinanceAssistant.Evals/FinanceAssistant.Evals.csproj
```

That second command is the only one that tells you whether `ProposeNextStepAsync` survived the replace. Drop the method and this build fails with `CS1061` while the first one still says `Build succeeded`. If you skipped P5.02, skip this command too.

Run the console app:

```bash
dotnet run --project src/FinanceAssistant
```

Ask something with a tool in it. Then ask for a transfer and decline it:

```
> How much did I spend on coffee this year?
[memory] 1 messages in history
[agent] iteration 1: calling SearchTransactions
[agent] iteration 2: final answer
You spent **$31.51 on coffee so far in 2026**, across **5 purchases**.

> Transfer 100 from Checking to Savings.
[memory] 5 messages in history
[agent] iteration 1: calling Transfer
[agent] 'Transfer' requires confirmation.
        Arguments: fromAccount=Checking, toAccount=Savings, amount=100
        Type 'yes' to proceed: no
[agent] iteration 2: final answer
The transfer was not completed because it was declined.
```

Compare the shape, not the numbers. Three things in that block move on their own. The model picks its own tools, so the coffee question can take two iterations or three, and a `calling GetTransactions` line after the search is a normal run rather than a bug. The total moves with the tools it picked. And the `[memory]` count on the second question is just however many messages the first turn left behind, so it follows the iteration count up and down.

What has to hold is P5.01's shape, and there are five parts to it:

- Every `[agent]` line still prints, one per tool call and one for the final answer.
- The confirmation prompt still appears, and still reads from stdin.
- `final answer` is still the last `[agent]` line before the reply.
- The reply doesn't open with text the model wrote before a tool call. That one is `RunTurnAsync` clearing the buffer, not the agent. The browser has no equivalent and will show you that chatter.
- On the transfer, the second `[agent]` line reads `iteration 2`, not `iteration 1`.

That last one is the exception to "compare the shape, not the numbers", and it's the only number in the block that's load-bearing. A transfer is two passes through the loop with a pause in the middle. If the second pass prints `iteration 1`, `_iteration` is a local variable rather than a field and the resume reset it. Nothing else in this run would tell you.

If one of those five is wrong, fix it before you write a line of web code. The console app is your regression test for this refactor, and it's a better one than it looks, because it exercises the interrupt and the resume without a browser in the way. It doesn't exercise the abandoned approval, because `ConfirmInteractive` always answers. Step 6 item 7 is where that gets tested.

---

## Step 3: One conversation per thread

Now the web project. Two types, in one file.

Create `src/FinanceAssistant.Web/ChatSession.cs`:

```csharp
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AGUI.Server;
using FinanceAssistant.Memory;
using Microsoft.Extensions.AI;

namespace FinanceAssistant.Web;

public sealed class ChatSession
{
    private readonly SemaphoreSlim _turnLock = new(1, 1);

    public ChatSession(IChatClient chatClient, ChatOptions options, string systemPrompt)
    {
        Agent = new ChatAgent(
            chatClient,
            options,
            new ConversationStore(),
            systemPrompt,
            reducer: new SummarizingHistoryReducer(chatClient));
    }

    public ChatAgent Agent { get; }

    public DateTimeOffset LastUsedUtc { get; private set; } = DateTimeOffset.UtcNow;

    public async IAsyncEnumerable<ChatResponseUpdate> RunAsync(
        ChatRequestContext context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        LastUsedUtc = DateTimeOffset.UtcNow;

        if (!await _turnLock.WaitAsync(TimeSpan.Zero, ct))
        {
            yield return new ChatResponseUpdate(
                ChatRole.Assistant,
                "This conversation is already answering something. Wait for it to finish.");
            yield break;
        }

        try
        {
            await foreach (var update in Agent.RunTurnStreamingAsync(context.Messages, ct))
            {
                yield return update;
            }
        }
        finally
        {
            LastUsedUtc = DateTimeOffset.UtcNow;
            _turnLock.Release();
        }
    }
}

public sealed class SessionRegistry(IChatClient chatClient, ChatOptions options, string systemPrompt)
{
    private readonly ConcurrentDictionary<string, ChatSession> _sessions = new();

    public ChatSession GetOrCreate(string threadId) =>
        _sessions.GetOrAdd(threadId, _ => new ChatSession(chatClient, options, systemPrompt));

    public int Sweep(TimeSpan idleFor)
    {
        var cutoff = DateTimeOffset.UtcNow - idleFor;
        var removed = 0;

        foreach (var (id, session) in _sessions)
        {
            if (session.LastUsedUtc < cutoff && _sessions.TryRemove(id, out _))
            {
                removed++;
            }
        }

        return removed;
    }
}
```

Five things worth reading carefully:

**There's no cookie, and no middleware to set one.** AG-UI puts a `threadId` in the request body, so conversation identity arrives with the question instead of being reconstructed from a header. The page generates one per tab in Step 5. That's a real simplification and not a shortcut: an earlier version of this exercise stamped a `Set-Cookie` from middleware, and it needed middleware precisely because the cookie had to exist before a long-lived streaming response started. The protocol having a field for this deletes the whole problem.

It's still a session id and not an identity. It says "same tab", nothing more. Anyone who sends your thread id gets your conversation. Real auth is out of scope here and is the first thing you would add.

**`ConversationStore` is a `List<ChatMessage>` with no lock, and that was always fine.** It was fine because one process meant one caller. On a web server, two requests for the same thread id would append to the same list from two thread-pool threads, and the failure mode isn't a clean exception. It's a conversation with a tool result that pairs with nothing, or an off-by-one in the reducer's tail. `SemaphoreSlim(1, 1)` with `TimeSpan.Zero` is the smallest honest fix: one turn at a time per session, and a second concurrent turn gets told so rather than queued. Queueing would be friendlier. Rejecting is more obvious when you're learning what the constraint is.

**Notice how long that lock is held now.** In the parked-`TaskCompletionSource` design, an approval card nobody answered held this semaphore for the full timeout, and the whole conversation was frozen behind one open dialog. Here the turn ends at the interrupt and the lock releases immediately. The pause costs nothing while it lasts, which is the practical argument for the interrupt model over the parked one, quite apart from the tidier code.

**`ChatOptions` is shared across every session. The store is not.** The tool list is immutable after startup and every session can read it. The message history is per-conversation by definition. Getting that split right is most of the work of turning a single-user app into a multi-user one, and it is worth staring at the constructor above until the split is obvious.

**`Sweep` is written and never called, on purpose.** A `ConcurrentDictionary` keyed by thread id grows forever, and every entry holds a full conversation history. That's a memory leak with a slow fuse. The method is here so the shape is visible. Wiring it to a `PeriodicTimer` in a `BackgroundService` is four lines and a good first extension. Naming the leak and leaving the fix as a stub is more honest than pretending a demo doesn't have one. And no, it doesn't trip `TreatWarningsAsErrors`. The C# compiler warns about unused *private* members. A public method with no callers is a library waiting for one, as far as it knows.

Build before you go on:

```bash
dotnet build
```

`Program.cs` is still the `Hello World!` one `dotnet new web` gave you, and that's fine. Two classes that nothing calls yet still have to compile, and finding a typo here beats finding it in Step 6 with a startup log scrolling past it.

---

## Step 4: The composition root

Replace `src/FinanceAssistant.Web/Program.cs`:

```csharp
using AGUI.Abstractions;
using AGUI.Server;
using FinanceAssistant;
using FinanceAssistant.Data;
using FinanceAssistant.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Pgvector;

using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddChatClient(builder.Configuration);
builder.Services.AddEmbeddingGenerator(builder.Configuration);

// AG-UI first, and AGUIJsonUtilities.DefaultTypeInfoResolver rather than the bare
// source-generated context. TypedResults.ServerSentEvents serialises with these application
// options, so two things have to hold for a field with no value to stay off the wire: the
// AG-UI resolver has to carry the omission, and it has to be asked before AIJsonUtilities'
// resolver, which answers for any type and would otherwise resolve AG-UI events itself.
builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AGUIJsonUtilities.DefaultTypeInfoResolver);
    options.SerializerOptions.TypeInfoResolverChain.Add(AIJsonUtilities.DefaultOptions.TypeInfoResolver!);
    AGUIJsonUtilities.RegisterInterruptContentTypes(options.SerializerOptions);
});

var systemPrompt = await File.ReadAllTextAsync(
    Path.Combine(AppContext.BaseDirectory, "Prompts", "SystemPrompt.md"));

builder.Services.AddSingleton(sp =>
{
    var embedder = sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
    var chatOptions = new ChatOptions { Tools = AgentToolset.CreateTools(embedder) };

    return new SessionRegistry(sp.GetRequiredService<IChatClient>(), chatOptions, systemPrompt);
});

var app = builder.Build();

// Same startup the console app runs: create the schema if it isn't there,
// then embed any transaction that doesn't have a vector yet.
await using (var db = new FinanceDbContext())
{
    await db.Database.EnsureCreatedAsync();

    var unembedded = await db.Transactions.Where(t => t.Embedding == null).ToListAsync();
    if (unembedded.Count > 0)
    {
        app.Logger.LogInformation("Embedding {Count} transactions...", unembedded.Count);
        var texts = unembedded.Select(t => $"{t.Merchant} {t.Description}").ToList();
        var embedder = app.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        var embeddings = await embedder.GenerateAsync(texts);
        for (var i = 0; i < unembedded.Count; i++)
        {
            unembedded[i].Embedding = new Vector(embeddings[i].Vector.ToArray());
        }
        await db.SaveChangesAsync();
    }
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/chat", (
    [FromBody] RunAgentInput input,
    SessionRegistry sessions,
    IOptions<JsonOptions> jsonOptions,
    CancellationToken ct) =>
{
    var context = input.ToChatRequestContext(jsonOptions.Value.SerializerOptions);
    var session = sessions.GetOrCreate(input.ThreadId);

    var events = session.RunAsync(context, ct).AsAGUIEventStreamAsync(context, ct);

    return TypedResults.ServerSentEvents(events);
});

app.Run("http://localhost:5080");
```

Build again before the page goes in:

```bash
dotnet build
```

Eight things worth reading carefully:

**One endpoint, five lines, and three of them are the protocol.** `ToChatRequestContext` turns the AG-UI request body into `ChatMessage` values and a `ChatOptions`. Your session runs the loop. `AsAGUIEventStreamAsync` turns the updates back into AG-UI events. The old shape of this exercise had two endpoints and a hand-written frame vocabulary. This has one endpoint and no vocabulary, because the vocabulary is the package.

**`AddChatClient` and `AddEmbeddingGenerator` are the ones you wrote in P1.02 and P2.02, unchanged.** They are extension methods on `IServiceCollection` taking an `IConfiguration`, and `WebApplicationBuilder` hands you both. The reasoning-effort pin from P1.02's `ConfigureOptions` comes along with them, so `GetStreamingResponseAsync` here inherits `ReasoningEffort.None` exactly like every other call in the workshop. That's the client-wide default earning its keep in a project that didn't exist when you set it.

**The JSON block is load-bearing and the comment is not decoration.** It's copied, with its reasoning, from the AG-UI repo's own sample. Both halves matter. `AIJsonUtilities.DefaultOptions.TypeInfoResolver` answers for any type at all, so if it's asked first it resolves AG-UI events itself and loses the "omit nulls" behaviour baked into the AG-UI context. The visible symptom is `"parentRunId": null` and `"input": null` appearing in your frames, which the TypeScript AG-UI clients reject outright. Your own page in Step 5 wouldn't care. A client you didn't write would.

**`RegisterInterruptContentTypes` is the approval half of that same block.** It teaches the serialiser about the interrupt content types, which is what lets a resume payload turn back into a `ToolApprovalResponseContent`. Leave it out and approvals fail quietly, in the worst way: the resume decodes as a generic interrupt, your `decisions` list is empty, and the agent treats a resume as a brand new turn with no user message.

**`AgentToolset.CreateTools` is the list the REPL and the evals already share.** P5.02 pulled the four registrations into one static method and wrote the reason in a comment above it: an eval that builds its own tool list grades a set the agent may no longer ship. A third front end is the moment that pays off. If you stopped at P5.01 and `AgentToolset` doesn't exist yet, build the array inline the way `FinanceAssistant/Program.cs` does, keeping `new ApprovalRequiredAIFunction(...)` around the transfer tool, and add `using FinanceAssistant.Tools;` at the top.

**`context.ChatOptions` is deliberately ignored, and it's worth knowing what's in it.** AG-UI lets the *client* declare tools, and `ToChatRequestContext` puts those on `context.ChatOptions`. Our tools are the server's, so the session uses its own options and the client's list goes nowhere. That's the right call for a finance agent. Merging the two is AG-UI's frontend-tools scenario, where the browser offers something only the browser can do, and it's in "What's next" for a reason.

**`TypedResults.ServerSentEvents` still does the whole transport.** AG-UI's SSE encoding is a sequence of `data: {json}` records with a blank line between them and no `event:` line, which is exactly what `TypedResults.ServerSentEvents` writes when you hand it events. It's new in .NET 10 along with the `System.Net.ServerSentEvents` types it uses. On .NET 9 neither exists in the shared framework and this endpoint is thirty lines of `Response.Body.WriteAsync` plus a `FlushAsync` you would forget exactly once.

**`app.Run("http://localhost:5080")` beats `launchSettings.json`.** `dotnet new web` generates a profile with a random port in it. The explicit URL here wins, so the address is stable no matter how you start the app. The startup log still says `Using launch settings from src/FinanceAssistant.Web/Properties/launchSettings.json...` and then `Now listening on: http://localhost:5080`, which looks like a contradiction and isn't. The profile is read, its `applicationUrl` is overridden. Port 5080 keeps out of the way of the MCP server on 5050 if you did P6.01.

The startup block is copy-pasted from the console app, on purpose, so the web project stands on its own and this guide doesn't send you back to edit `FinanceAssistant/Program.cs` again. If you keep both front ends past today, lift it into an `EnsureDatabaseAsync` extension in the `FinanceAssistant` project and call it from both.

---

## Step 5: The page

Three files under `src/FinanceAssistant.Web/wwwroot/`. Create the folder first. `dotnet new web` does not:

```bash
mkdir -p src/FinanceAssistant.Web/wwwroot
```

### 5.1: wwwroot/index.html

```html
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Finance Assistant</title>
  <link rel="stylesheet" href="app.css" />
</head>
<body>
  <main>
    <h1>Finance assistant</h1>
    <div id="log" aria-live="polite"></div>
    <form id="composer">
      <input id="input" autocomplete="off" placeholder="Ask about your spending..." />
      <button id="send" type="submit">Send</button>
    </form>
  </main>
  <script src="app.js"></script>
</body>
</html>
```

### 5.2: wwwroot/app.css

```css
:root { color-scheme: light dark; --line: color-mix(in srgb, currentColor 15%, transparent); }
* { box-sizing: border-box; }
body { margin: 0; font: 16px/1.5 ui-sans-serif, system-ui, sans-serif; }
main { max-width: 44rem; margin: 0 auto; padding: 1.5rem 1rem 6rem; }
h1 { font-size: 1.1rem; font-weight: 600; margin: 0 0 1.5rem; }
#log { display: flex; flex-direction: column; gap: 1rem; }
.msg { padding: .75rem 1rem; border-radius: .75rem; white-space: pre-wrap; word-wrap: break-word; }
.user { background: color-mix(in srgb, currentColor 8%, transparent); align-self: flex-end; max-width: 80%; }
.assistant { border: 1px solid var(--line); }
.tool { font: .8rem ui-monospace, monospace; opacity: .65; }
.approval { border: 1px solid var(--line); border-left: 3px solid orange; padding: .75rem 1rem; border-radius: .5rem; }
.approval code { display: block; font-size: .8rem; opacity: .75; margin: .35rem 0 .75rem; overflow-x: auto; }
.approval button { margin-right: .5rem; padding: .35rem .9rem; border-radius: .4rem; cursor: pointer; }
#composer { position: fixed; bottom: 0; left: 0; right: 0; display: flex; gap: .5rem;
  padding: 1rem; background: Canvas; border-top: 1px solid var(--line); }
#composer > * { padding: .6rem .9rem; border: 1px solid var(--line); border-radius: .5rem; font: inherit; }
#input { flex: 1; min-width: 0; }
#send { cursor: pointer; }
```

### 5.3: wwwroot/app.js

```js
const log = document.getElementById("log");
const form = document.getElementById("composer");
const input = document.getElementById("input");
const send = document.getElementById("send");

// One thread per tab. The server keys its ConversationStore on this, so it is the
// whole of "whose conversation is this".
const threadId = sessionStorage.getItem("fa.threadId") ?? crypto.randomUUID();
sessionStorage.setItem("fa.threadId", threadId);

form.addEventListener("submit", async (event) => {
  event.preventDefault();
  const message = input.value.trim();
  if (!message) return;

  input.value = "";
  bubble("user").textContent = message;

  await run({
    messages: [{ id: crypto.randomUUID(), role: "user", content: message }]
  });
});

async function run(body) {
  send.disabled = true;

  // TOOL_CALL_START names a call, TOOL_CALL_ARGS streams its arguments, and an interrupt
  // arrives later referencing only the id. Keep the pieces so the approval card can show
  // what it is approving.
  const calls = new Map();
  let assistant = null;

  try {
    for await (const event of stream(body)) {
      switch (event.type) {
        case "TEXT_MESSAGE_START":
          assistant = bubble("assistant");
          break;

        case "TEXT_MESSAGE_CONTENT":
          assistant ??= bubble("assistant");
          assistant.textContent += event.delta;
          break;

        case "TEXT_MESSAGE_END":
          assistant = null;
          break;

        case "TOOL_CALL_START":
          assistant = null;
          calls.set(event.toolCallId, { callId: event.toolCallId, name: event.toolCallName, args: "" });
          bubble("tool").textContent = `calling ${event.toolCallName}...`;
          break;

        case "TOOL_CALL_ARGS": {
          const call = calls.get(event.toolCallId);
          if (call) call.args += event.delta;
          break;
        }

        case "RUN_FINISHED":
          if (event.outcome?.type === "interrupt") {
            for (const interrupt of event.outcome.interrupts ?? []) {
              renderApproval(interrupt, calls.get(interrupt.toolCallId));
            }
          }
          break;

        case "RUN_ERROR":
          bubble("tool").textContent = `Run failed: ${event.message}`;
          break;
      }
    }
  } catch (err) {
    bubble("tool").textContent = `Connection lost: ${err.message}`;
  } finally {
    send.disabled = false;
    input.focus();
  }
}

async function* stream(body) {
  const response = await fetch("/api/chat", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      threadId,
      runId: crypto.randomUUID(),
      messages: [],
      state: {},
      tools: [],
      context: [],
      forwardedProps: {},
      ...body
    })
  });

  if (!response.ok) throw new Error(`HTTP ${response.status}`);

  const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
  let buffer = "";

  while (true) {
    const { value, done } = await reader.read();
    if (done) break;

    buffer += value;

    let split;
    while ((split = buffer.indexOf("\n\n")) >= 0) {
      const frame = buffer.slice(0, split);
      buffer = buffer.slice(split + 2);

      const data = frame
        .split("\n")
        .filter((line) => line.startsWith("data:"))
        .map((line) => line.slice(5).trim())
        .join("\n");

      if (data) yield JSON.parse(data);
    }
  }
}

function renderApproval(interrupt, call) {
  let args = {};
  try {
    args = JSON.parse(call?.args || "{}");
  } catch {
    args = {};
  }

  const pretty = Object.entries(args).map(([key, value]) => `${key}=${value}`).join(", ");

  const box = document.createElement("div");
  box.className = "approval";
  box.append(document.createElement("strong"), " needs your approval.", document.createElement("code"));
  box.querySelector("strong").textContent = call?.name ?? "This tool";
  box.querySelector("code").textContent = pretty || "(no arguments)";

  for (const [label, approved] of [["Approve", true], ["Decline", false]]) {
    const button = document.createElement("button");
    button.textContent = label;
    button.addEventListener("click", async () => {
      box.querySelectorAll("button").forEach((b) => (b.disabled = true));
      box.append(approved ? " Approved." : " Declined.");

      // No new message. The run resumes from the interrupt the server is holding.
      await run({
        messages: [],
        resume: [{
          interruptId: interrupt.id,
          status: "resolved",
          payload: {
            approved,
            toolCall: { callId: call?.callId ?? interrupt.toolCallId, name: call?.name, arguments: args }
          }
        }]
      });
    });
    box.append(button);
  }

  log.append(box);
  box.scrollIntoView({ block: "end" });
}

function bubble(kind) {
  const el = document.createElement("div");
  el.className = `msg ${kind}`;
  log.append(el);
  el.scrollIntoView({ block: "end" });
  return el;
}
```

Nine things worth reading carefully:

**`EventSource` isn't used, and can't be.** The browser's built-in SSE client only issues `GET` requests and can't set a request body. An AG-UI run starts with a `POST`, so we read the response body ourselves. The parser is the loop inside `stream()`, and it's four moves: buffer the bytes, split on a blank line, keep the `data:` lines, `JSON.parse`. SSE is a genuinely small format and this is nearly all of it.

**`run()` is called from two places and that's the whole approval flow.** The composer calls it with a user message. An Approve or Decline button calls it with a `resume` array and no message at all. Same function, same stream reader, same rendering. The button doesn't resume anything by itself: it starts a second run that the server recognises as a continuation, because `ToChatRequestContext` decodes that `resume` entry back into the approval response your loop is looking for.

**The resume payload has to carry `toolCall`, and it's easy to get wrong.** The SDK decides whether a resume is a tool approval or a generic interrupt by checking for a `toolCall` field, and a payload of just `{ approved: true }` takes the generic path silently. So the card echoes back the call id, the name, and the parsed arguments. It can, because `TOOL_CALL_START` and `TOOL_CALL_ARGS` for that call arrive before the interrupt does. Note `callId`, not `id`, and note that `arguments` is a parsed object rather than the JSON string the deltas concatenate into. That `JSON.parse` at the top of `renderApproval` is doing two jobs.

The echo isn't only a routing hint, though. Those arguments are the ones the server invokes, which is the point 2.2 makes about `decision.ToolCall`. This page sends back what it was shown, so the two agree. Nothing in the endpoint checks that they do.

**`TOOL_CALL_ARGS` arrives whole here, even though the comment says it streams.** That's the protocol's shape, not this server's. Step 2.2 holds tool-call fragments back until `ToChatResponse()`, so one call produces exactly one `TOOL_CALL_ARGS` frame and the `+=` concatenates a single string. Point another AG-UI server at this page and the deltas arrive in pieces, which is why the accumulator is written the way it is.

**The `calls` Map exists because AG-UI events are small on purpose.** An interrupt names a `toolCallId` and nothing else about the call. The protocol assumes you were listening earlier, which is a reasonable assumption for a streaming protocol and a trap for anyone who starts reading events halfway through. Twelve lines of bookkeeping is the price.

**`textContent`, never `innerHTML`, for anything downstream of the model.** There's no `innerHTML` in this file at all, including for the tool name, which is server-constrained and would in fact have been safe. That's on purpose. "This particular value is safe" is a claim that has to be re-checked every time the code around it changes, and the one place it stops being true is the place nobody re-checks. Building the card out of `createElement` and `textContent` costs one extra line and removes the claim entirely. Model output is untrusted input. It can contain a `<script>` tag because a transaction description contained one, or because somebody asked it to.

**The approve `fetch` still handles failure, and there's only one HTTP way left to fail.** The old two-endpoint design could return a `404` for a missing session or a `409` for an approval nobody was waiting on, and both came back as an ordinary response with `ok: false` and no exception. Resuming through the same endpoint deletes both status codes: there's no separate session lookup to miss. What's left for the client to handle is `!response.ok` and a dropped connection, which is what the `if` and the `catch` in `stream()` cover.

Read that as a statement about the client, not about the server. The two error codes are gone because nothing checks any more. A resume naming an interrupt this agent never issued isn't rejected. It decodes into a decision and the loop runs it, on a thread id that has never been used, which is the hole 2.2 describes and item 6b of Step 6 makes you watch happen. The endpoint got simpler by giving up the two checks that were failing safely.

**Nothing renders `TOOL_CALL_RESULT`, and you'll notice after an approval.** The `switch` handles text, tool calls, arguments and the interrupt, and drops the result on the floor. So after you click Approve the page shows "Approved." and then the assistant's sentence, with nothing in between confirming that the tool ran. It did run. The proof is on the wire, which is what item 6 of Step 6 has you look at. Adding a `case` for it is four lines, and leaving it out keeps the page honest about being a demo.

**The page shows you raw Markdown, on purpose.** `.msg` is `white-space: pre-wrap` and the text arrives through `textContent`, so an answer the model wrote as `**$31.51**` renders with the asterisks showing. Rendering it means running a Markdown parser over untrusted model output and then setting `innerHTML`, which is the exact thing the point above says not to do. Reach for a parser that sanitises, or leave the asterisks. Leaving them is a fine answer for a demo.

---

## Step 6: Run

Make sure the container is up, then start the web app:

```bash
docker compose up -d
```

```bash
dotnet run --project src/FinanceAssistant.Web
```

Open `http://localhost:5080`. Seven things to try, in order, and item 6 comes in two halves.

No browser to hand, or working over SSH? Item 5 defines a shell helper that posts the same requests a tab does, and items 2, 3 and 4 each have a one-line equivalent through it. Item 1 does not. The helper hides the per-token text frames, which are the whole of what item 1 asks you to watch, so use the timing command in the first Troubleshooting entry instead. It measures the arrival spread directly. Item 4's Decline is item 6's resume command with `"approved":false`.

**1. A plain question.** Type `hello`, then ask `Explain compound interest in three paragraphs` and watch what arrives.

Be ready for the answer to land in one block after a pause. On the workshop's Azure deployment it does, every time, and your code isn't the reason. Azure OpenAI runs content filtering synchronously unless the deployment is configured otherwise, so the service buffers the stream and hands it over in batches. Read the first Troubleshooting entry before you go hunting, because this is the one place in the exercise where correct code looks broken.

**2. A question that needs a tool.** Type `How much did I spend on coffee this year?`. You should see a monospace `calling ...` line naming whichever tool the model picked (usually `SearchTransactions`, sometimes `GetTransactions`), then the answer underneath it. Now look at the terminal running the server. No `[agent]` lines. They live in the console front end now, and the browser is rendering the same updates as a tool bubble. Which is the point.

The one line that does still turn up there is `[memory] reducing N messages into 1 summary`, once the conversation is long enough to trip the reducer. That's the fifth console leak from the top of this guide, printing from `SummarizingHistoryReducer` rather than from `ChatAgent`. Seeing it here is the cheapest possible demonstration of why a second front end finds things a test suite doesn't.

**3. A follow-up.** Type `What was the coffee total again?`. The agent answers from history, with no `calling` line, because `ConversationStore` is doing the same job it did in P4.01. It's keyed on your tab's thread id now instead of on nothing at all.

**4. The transfer.** Type `Transfer 100 from Checking to Savings.` You get a `calling Transfer...` line first, then a card:

```
calling Transfer...

Transfer needs your approval.
fromAccount=Checking, toAccount=Savings, amount=100
[ Approve ] [ Decline ]
```

Click **Approve**. A second run starts, the tool executes, and the agent confirms the transfer. That button sends the request item 6 has you type by hand, field for field, so running that command is clicking this button. Now ask for another transfer and click **Decline**. The agent should tell you it didn't transfer anything and shouldn't retry with different arguments, which is the `user_declined` payload from P5.01 doing its job through a completely different prompt surface.

**5. A second user.** Open a new tab and type the address in. `sessionStorage` is per tab, so a new tab gets its own thread id and its own conversation.

Do not use Duplicate Tab. It copies `sessionStorage` along with everything else, so the copy keeps the same thread id, answers from history, and looks like this whole item is broken.

Ask the new tab `What was the coffee total again?`.

It doesn't say it has no idea. It goes and looks the answer up again, with a worse query, because the word "again" carried no context into a conversation that has none. Expect a different number, confidently stated. That difference isn't the thing to look at, and it's the thing people look at.

Look for the tool call instead. Item 3 asked this same question in the first tab and got no `calling` line at all, because the answer was already in that tab's history. Here you get one. Present in the second tab, absent in the first, for the same question. That pair is the whole of the multi-user behaviour. Different tab, different thread id, different `ChatSession`, different `ConversationStore`.

This is easier to see from the terminal than the old cookie-jar version was, because the thread id is just a field you control. Every command from here to the end of Step 6 posts a run and reads the events back, so define this once, in the terminal you will run them from.

**A terminal for the rest of Step 6.** This is the first thing in the guide that isn't a `dotnet` command, and it's bash or zsh. It needs `curl` and `python3` on your PATH. On Windows, run it from WSL or Git Bash, because PowerShell aliases `curl` to `Invoke-WebRequest` and doesn't take this function syntax.

```bash
agui() {
  curl -s -N -X POST http://localhost:5080/api/chat \
    -H 'Content-Type: application/json' \
    -d "$1" \
  | python3 -c "
import sys, json
for line in sys.stdin:
    line = line.strip()
    if not line.startswith('data:'): continue
    e = json.loads(line[5:]); e.pop('rawEvent', None)
    if e.get('type') != 'TEXT_MESSAGE_CONTENT': print(json.dumps(e)[:int(sys.argv[1])])
" "${2:-160}"
}
```

It posts the JSON you give it, drops the `rawEvent` echo, hides the per-token text frames, and cuts each line to 160 characters. Pass a width as a second argument when a line is worth reading in full. The function lives in that shell only. Open a new terminal and define it again.

Without it the coffee question prints north of a hundred kilobytes for a five-line answer. About half of that is the `rawEvent` echo and most of the rest is a single tool result. The Troubleshooting entry near the end says why the echo is there.

Now the second conversation:

```bash
agui '{"threadId":"somebody-else","runId":"r1","messages":[{"id":"m1","role":"user","content":"What was the coffee total again?"}],"state":{},"tools":[],"context":[],"forwardedProps":{}}'
```

The event to look for is `TOOL_CALL_START` carrying `"toolCallName": "SearchTransactions"`. That's the same thing the browser draws as `calling SearchTransactions...`, and it's the event your first tab didn't produce for this question.

**6. Read the wire.** This is the step that pays for the whole exercise, so do not skip it. Ask a tool question:

```bash
agui '{"threadId":"wire","runId":"r1","messages":[{"id":"m1","role":"user","content":"How much did I spend on coffee this year?"}],"state":{},"tools":[],"context":[],"forwardedProps":{}}'
```

```
{"type": "RUN_STARTED", "threadId": "wire", "runId": "r1"}
{"type": "TOOL_CALL_START", "toolCallId": "call_qj5dJsOWzkBFssuHvtMM44Do", "toolCallName": "SearchTransactions"}
{"type": "TOOL_CALL_ARGS", "toolCallId": "call_qj5dJsOWzkBFssuHvtMM44Do", "delta": "{\"query\":\"coffee purchases this year 2026\",\"topK\":20}"}
{"type": "TOOL_CALL_END", "toolCallId": "call_qj5dJsOWzkBFssuHvtMM44Do"}
{"type": "TOOL_CALL_RESULT", "messageId": "call_qj5dJsOWzkBFssuHvtMM44Do", "toolCallId": "call_qj5dJsOWzkBFssuHvtMM44Do", "content": "{\n  \"matches\": [\n    {
{"type": "TEXT_MESSAGE_START", "messageId": "chatcmpl-EK9XS5buVbn0Ek2JKhpURLLFV1FrR", "role": "assistant"}
{"type": "TEXT_MESSAGE_END", "messageId": "chatcmpl-EK9XS5buVbn0Ek2JKhpURLLFV1FrR"}
{"type": "RUN_FINISHED", "threadId": "wire", "runId": "r1", "outcome": {"type": "success"}}
```

You may get a second `START` / `ARGS` / `END` / `RESULT` group for `GetTransactions` before the text events. Same shape, one more pass through the loop, and the same eight event types your filter lets through. There's a ninth on the wire, `TEXT_MESSAGE_CONTENT`, and `agui` hides it because a five-line answer arrives as seventy-odd of them.

Nothing in that output is yours. Every name in it is in a spec, and a client that has never heard of the finance assistant can render all of it. Compare it against the table in 2.1 and you can point at which line of `ChatAgent` produced each event. That `TOOL_CALL_RESULT` in the middle is the one to look at hardest: your loop invoked the tool and yielded a synthesised update carrying `FunctionResultContent`, and the adapter turned it into a standard event. The model never produced it.

Now do the same for a transfer and watch the run end differently. Use a fresh thread id, and widen the output, because the interrupt is the longest event in this exercise:

```bash
agui '{"threadId":"t3","runId":"r1","messages":[{"id":"m1","role":"user","content":"Transfer 100 from Checking to Savings."}],"state":{},"tools":[],"context":[],"forwardedProps":{}}' 400
```

The last two events are the ones that matter. The second one arrives as a single line, wrapped here to fit the page:

```
{"type": "TOOL_CALL_END", "toolCallId": "call_tuXTwuRrUmeJD2YWiXEyqLhb"}
{"type": "RUN_FINISHED", "threadId": "t3", "runId": "r1", "outcome": {"type": "interrupt", "interrupts":
  [{"id": "call_tuXTwuRrUmeJD2YWiXEyqLhb", "reason": "tool_call",
    "message": "Approval required for tool call: Transfer",
    "toolCallId": "call_tuXTwuRrUmeJD2YWiXEyqLhb",
    "responseSchema": {"type": "object", "properties": {"approved": {"type": "boolean"}}, "required": ["approved"]}}]}}
```

The run *finished*. It didn't fail, and nothing is being held open on the server. The interrupt tells the client what it has to answer and hands it a JSON Schema for the answer.

Resume it by hand. `PASTE_CALL_ID` appears twice. Replace both, with the `toolCallId` from your own output:

```bash
agui '{"threadId":"t3","runId":"r2","messages":[],"resume":[{"interruptId":"PASTE_CALL_ID","status":"resolved","payload":{"approved":true,"toolCall":{"callId":"PASTE_CALL_ID","name":"Transfer","arguments":{"fromAccount":"Checking","toAccount":"Savings","amount":100}}}}],"state":{},"tools":[],"context":[],"forwardedProps":{}}'
```

```
{"type": "RUN_STARTED", "threadId": "t3", "runId": "r2"}
{"type": "TOOL_CALL_RESULT", "messageId": "call_tuXTwuRrUmeJD2YWiXEyqLhb", "toolCallId": "call_tuXTwuRrUmeJD2YWiXEyqLhb", "content": "{\n  \"transferred\": 100,
{"type": "TEXT_MESSAGE_START", "messageId": "chatcmpl-EKJ4CcpufGOguAq94TpMUIh2Q2648", "role": "assistant"}
{"type": "TEXT_MESSAGE_END", "messageId": "chatcmpl-EKJ4CcpufGOguAq94TpMUIh2Q2648"}
{"type": "RUN_FINISHED", "threadId": "t3", "runId": "r2", "outcome": {"type": "success"}}
```

`TOOL_CALL_RESULT` as the first event after `RUN_STARTED` is the check the Troubleshooting section names for a resume that decoded correctly.

**6b. Now abuse it.** Three runs. The first two are why 2.2 spent a bullet on the resume payload, and the third is what Step 5's approve-`fetch` bullet warned about.

Two ids travel in a resume and they do different jobs. `interruptId` is how the SDK finds the interrupt. `payload.toolCall.callId` is what the tool result gets filed under. Get the first right, leave the second wrong, and watch.

Raise one more interrupt, on a thread id you're willing to destroy:

```bash
agui '{"threadId":"t5","runId":"r1","messages":[{"id":"m1","role":"user","content":"Transfer 100 from Checking to Savings."}],"state":{},"tools":[],"context":[],"forwardedProps":{}}' 400
```

Now resume it. This block has two different placeholders on purpose. Replace `PASTE_CALL_ID` with the real id and leave `LEAVE_THIS_ALONE` exactly as it is:

```bash
agui '{"threadId":"t5","runId":"r2","messages":[],"resume":[{"interruptId":"PASTE_CALL_ID","status":"resolved","payload":{"approved":true,"toolCall":{"callId":"LEAVE_THIS_ALONE","name":"Transfer","arguments":{"fromAccount":"Checking","toAccount":"Savings","amount":100}}}}],"state":{},"tools":[],"context":[],"forwardedProps":{}}'
```

```
{"type": "RUN_STARTED", "threadId": "t5", "runId": "r2"}
{"type": "TOOL_CALL_RESULT", "messageId": "LEAVE_THIS_ALONE", "toolCallId": "LEAVE_THIS_ALONE", "content": "{\n  \"transferred\": 100,\n  \"from\": \"Checking\"
{"type": "RUN_ERROR", "message": "An error occurred while streaming the agent response.", "code": "StreamingError"}
```

The resume still decoded as an approval, so the transfer still ran, with a real transaction id. But the result was filed under the literal string `LEAVE_THIS_ALONE`, which pairs with no call in the store, so the very next model call is rejected. That call is the one in this same run, which is why the `RUN_ERROR` lands directly under the tool result with no `RUN_FINISHED` after it. Money moved, thread dead, and the `messageId` reading `LEAVE_THIS_ALONE` is sitting one line above the error.

That's the `CallId` half of what 2.2 described. Here's the other half. Ask for one more transfer, on another fresh thread id:

```bash
agui '{"threadId":"t4","runId":"r1","messages":[{"id":"m1","role":"user","content":"Transfer 100 from Checking to Savings."}],"state":{},"tools":[],"context":[],"forwardedProps":{}}' 400
```

Now approve it, with both placeholders replaced and the arguments rewritten:

```bash
agui '{"threadId":"t4","runId":"r2","messages":[],"resume":[{"interruptId":"PASTE_CALL_ID","status":"resolved","payload":{"approved":true,"toolCall":{"callId":"PASTE_CALL_ID","name":"Transfer","arguments":{"fromAccount":"Savings","toAccount":"Elsewhere","amount":999999}}}}],"state":{},"tools":[],"context":[],"forwardedProps":{}}' 300
```

```
{"type": "RUN_STARTED", "threadId": "t4", "runId": "r2"}
{"type": "TOOL_CALL_RESULT", "messageId": "call_phb5nlJsTp97KiYom7Hefuo1", "toolCallId": "call_phb5nlJsTp97KiYom7Hefuo1", "content": "{\n  \"transferred\": 999999,\n  \"from\": \"Savings\",\n  \"to\": \"Elsewhere\",\n  \"transactionId\": \"2a6b6ab1-e10a-4028-b1e4-539945db00db\"\n}"}
{"type": "TEXT_MESSAGE_START", "messageId": "chatcmpl-EKKR9UPHkpCBxqIRBRWQG17AVdaKm", "role": "assistant"}
{"type": "TEXT_MESSAGE_END", "messageId": "chatcmpl-EKKR9UPHkpCBxqIRBRWQG17AVdaKm"}
{"type": "RUN_FINISHED", "threadId": "t4", "runId": "r2", "outcome": {"type": "success"}}
```

You approved 100 to Savings. 999999 left for somewhere else, with a real transaction id. The gate fired, a human answered it, and the answer carried the payload. The model may well notice and say so in its reply, which is worth reading and isn't a control.

Note the last line. This run ends `success`, where the one above it ended in `RUN_ERROR`. A wrong call id breaks the thread and tells you something is wrong. Wrong arguments under a correct call id leave no trace at all.

One more, and this is the one to remember. Both runs above resumed an interrupt the agent really had raised. Do not raise one at all. Invent the thread id, invent the call id, and post the resume cold:

```bash
agui '{"threadId":"never-seen-before","runId":"r1","messages":[],"resume":[{"interruptId":"call_TOTALLY_MADE_UP","status":"resolved","payload":{"approved":true,"toolCall":{"callId":"call_TOTALLY_MADE_UP","name":"Transfer","arguments":{"fromAccount":"Savings","toAccount":"Elsewhere","amount":4242}}}}],"state":{},"tools":[],"context":[],"forwardedProps":{}}' 300
```

```
{"type": "RUN_STARTED", "threadId": "never-seen-before", "runId": "r1"}
{"type": "TOOL_CALL_RESULT", "messageId": "call_TOTALLY_MADE_UP", "toolCallId": "call_TOTALLY_MADE_UP", "content": "{\n  \"transferred\": 4242,\n  \"from\": \"Savings\",\n  \"to\": \"Elsewhere\",\n  \"transactionId\": \"b6811bb2-63c1-42a1-8c8b-75bc9d3cbbf4\"\n}"}
{"type": "RUN_ERROR", "message": "An error occurred while streaming the agent response.", "code": "StreamingError"}
```

Read the middle line first. No conversation, no approval, no user, and the tool ran anyway, with a real transaction id. `POST /api/chat` carrying a `resume` array is a tool-execution endpoint that anyone can reach. That's the same missing check as the two runs above, with everything else stripped away.

The `RUN_ERROR` underneath is a different failure from the first demo, and the distinction is worth thirty seconds. There, a tool result pointed at a call id that didn't match the call above it. Here the store holds the system prompt and then a tool result, with no assistant message above it at all, so the API rejects the shape rather than the id:

```
Parameter: messages.[1].role
Invalid parameter: messages with role 'tool' must be a response to a preceeding message with 'tool_calls'.
```

Both errors arrive after the transfer, which is the only part that matters.

The fix is the store lookup 2.2 describes, and it's deliberately not in the listing, because seeing these three run is the only way it lands.

**7. Break it on purpose.** Ask for a transfer, then walk away from the card and ask a different question in the same tab. It answers normally, because `CloseDanglingCalls` declined the abandoned call before your new message went into the store. Now comment out the `CloseDanglingCalls()` call in the `else` branch of `RunTurnStreamingAsync`. Stop the app with `Ctrl+C` and start it again, because `dotnet run` isn't `dotnet watch` and a second one fails on port 5080. Then do it again. You get this:

```
{"type": "RUN_ERROR", "message": "An error occurred while streaming the agent response.", "code": "StreamingError"}
```

That message is all you get, the server log stays empty, and the session is finished for as long as the process lives. Twenty lines of guide told you this would happen. Watching it happen is worth more, and the Troubleshooting entry below explains why the error is so unhelpful and how to see the real one.

Put the `CloseDanglingCalls()` line back. Then restart the web app.

Then prove the restore worked, with the same two commands on a **new** thread id:

```bash
agui '{"threadId":"t9","runId":"r1","messages":[{"id":"m1","role":"user","content":"Transfer 100 from Checking to Savings."}],"state":{},"tools":[],"context":[],"forwardedProps":{}}'
```

```bash
agui '{"threadId":"t9","runId":"r2","messages":[{"id":"m2","role":"user","content":"What is 2+2?"}],"state":{},"tools":[],"context":[],"forwardedProps":{}}'
```

```
{"type": "RUN_STARTED", "threadId": "t9", "runId": "r2"}
{"type": "TEXT_MESSAGE_START", "messageId": "chatcmpl-EKJ2mQ4vRnT8sLpXbWzKcYdHfGa3N", "role": "assistant"}
{"type": "TEXT_MESSAGE_END", "messageId": "chatcmpl-EKJ2mQ4vRnT8sLpXbWzKcYdHfGa3N"}
{"type": "RUN_FINISHED", "threadId": "t9", "runId": "r2", "outcome": {"type": "success"}}
```

A fresh thread id is the safe habit rather than a requirement, and it's worth knowing which. Sessions live in memory, so the restart you just did cleared every one of them, the broken thread included. Reload the code without restarting the process, as `dotnet watch` would, and the thread you broke stays broken. You would then be debugging a fix that already worked.

Do not skip the check itself, though. The console check below passes with the line still commented out, because `ConfirmInteractive` always answers and the abandoned-approval path never runs. A regression test only covers the path it walks, and you just watched this one walk past a broken agent.

Last check: the console app still works.

```bash
dotnet run --project src/FinanceAssistant
```

Same prompts, same `[agent]` lines, same confirmation gate on stdin.

---

## Troubleshooting

### The answer appears all at once instead of streaming

Read this one before you change any code. On the workshop deployment it's the expected behaviour, and three of the four causes below aren't your fault.

**The model service is buffering, which is the likely one.** Azure OpenAI applies content filtering synchronously by default, and a synchronous filter has to see output before it can release it. Time the arrivals yourself, with the web app running:

```bash
curl -s -N -X POST http://localhost:5080/api/chat \
  -H 'Content-Type: application/json' \
  -d '{"threadId":"timing","runId":"r1","messages":[{"id":"m1","role":"user","content":"Explain compound interest in three paragraphs."}],"state":{},"tools":[],"context":[],"forwardedProps":{}}' \
| python3 -u -c "
import sys, time, json
start = time.monotonic()
stamps = []
for line in sys.stdin:
    if not line.startswith('data:'): continue
    if json.loads(line[5:]).get('type') == 'TEXT_MESSAGE_CONTENT':
        stamps.append(time.monotonic() - start)
if stamps:
    print(f'{len(stamps)} events, first {stamps[0]:.2f}s, last {stamps[-1]:.2f}s, spread {stamps[-1]-stamps[0]:.3f}s')
"
```

Measured against the workshop endpoint, three times in a row:

```
154 events, first 2.16s, last 2.17s, spread 0.011s
255 events, first 2.62s, last 2.63s, spread 0.011s
197 events, first 2.38s, last 2.39s, spread 0.008s
```

Two hundred-odd separate `TEXT_MESSAGE_CONTENT` frames, every one of them arriving inside a hundredth of a second, after a two-second wait. Your own numbers will differ, and the spread can run into tenths of a second rather than hundredths. The ratio is the tell, not the figure: a wait measured in seconds, then the whole answer inside a fraction of one. The events are real and the streaming path works. The service simply released the whole answer at once. The fix is on the deployment (asynchronous content filtering, which Azure gates behind a request) and not in your code.

Two things tell you this is what you're looking at. The gap is at the front and never spread through the answer. And tool-call events still arrive at the moment the tool runs, well before the final text, because those are separate model calls.

**The reply was genuinely short.** One or two fragments finish instantly. Ask for three paragraphs and compare.

**A proxy is buffering.** Put nginx in front of this and it buffers `text/event-stream` unless you set `X-Accel-Buffering: no` on the response or turn `proxy_buffering` off. Kestrel on `localhost` doesn't buffer, so this only applies once you deploy it.

**Response compression is on** and holding bytes until its buffer fills. SSE and compression middleware have to be configured together, and the fix is to exclude `text/event-stream`.

### `RUN_ERROR` with "An error occurred while streaming the agent response." and nothing in the log

`AsAGUIEventStreamAsync` hides the underlying error. It wraps the stream, catches exceptions, and emits a fixed, sanitised `RUN_ERROR`. The upstream code keeps exception details on the server and keeps wire errors stable. That is sensible for a protocol. In version `0.0.6`, though, it does not log the exception either. It catches and drops it.

Two ways to see the real one, and the first has limits worth knowing before you trust it.

The quick way is to ask the same thing through the console app, which doesn't go through the adapter and will print the exception. That works for a tool or a prompt that throws on every call. It can't tell you anything about a *particular* web thread, because the console builds its own `ConversationStore` in its own process and never sees the broken history. And it can't reproduce the abandoned approval at all, because `ConfirmInteractive` always answers. For those two, the console gives you a clean run and a false all-clear.

The direct way is a temporary endpoint that runs the session without the adapter, so the exception reaches ASP.NET Core and the developer exception page:

```csharp
app.MapPost("/api/raw", async (
    [FromBody] RunAgentInput input,
    SessionRegistry sessions,
    IOptions<JsonOptions> jsonOptions,
    CancellationToken ct) =>
{
    var context = input.ToChatRequestContext(jsonOptions.Value.SerializerOptions);
    var session = sessions.GetOrCreate(input.ThreadId);
    await foreach (var _ in session.RunAsync(context, ct)) { }
    return Results.Ok();
});
```

Post the same body there and read the stack trace. Delete it afterwards.

### `An assistant message with 'tool_calls' must be followed by tool messages`

That thread's history has an assistant message asking for a tool call and no result paired to it, so the API rejects the conversation before the model ever sees it. Through the endpoint it shows up as the generic `RUN_ERROR` above. Through `/api/raw` it reads:

```
System.ClientModel.ClientResultException: HTTP 400 (invalid_request_error: )
Parameter: messages.[5].role

An assistant message with 'tool_calls' must be followed by tool messages responding to each
'tool_call_id'. The following tool_call_ids did not have response messages: call_gz0AGV6YlNHLyiQLrgVx4x1l
```

Nothing recovers that thread on its own. It stays broken for as long as the process lives, and a new thread id is the only way back in.

You get there by abandoning an approval card and then asking something else. Check two things in `RunTurnStreamingAsync`. First, that `CloseDanglingCalls` is called at all. Second, and this is the one people get wrong, that in the `else` branch it runs *before* `AppendResponseMessages`. A tool result pairs with the call directly above it, so closing the orphan after the new user message has landed produces exactly the same 400 you were trying to prevent.

### The approval card appears, you click Approve, and the agent starts a fresh turn instead of transferring

The resume decoded as a generic interrupt rather than a tool approval, so `decisions` was empty and the loop took the new-turn path. Two candidates, both in the payload.

The payload is missing `toolCall`, or spells it wrong. The SDK checks for that exact field to decide which kind of resume this is, and falls back silently when it is absent. Inside it the id field is `callId`, not `id`.

Or `RegisterInterruptContentTypes` is missing from the `JsonOptions` block in Step 4. Without it the serialiser has no contract for the interrupt content types and the decode can't produce a `ToolApprovalResponseContent`.

Either way the request succeeds and the stream looks healthy, which is what makes this one expensive. Check the store: a correct resume produces a `TOOL_CALL_RESULT` as the first event of the second run.

### Frames contain `"parentRunId": null` and a third-party client rejects them

The JSON resolver chain in Step 4 is in the wrong order, or the AG-UI resolver is missing from it. `AIJsonUtilities.DefaultOptions.TypeInfoResolver` answers for every type, so if it's reached first it serialises AG-UI events itself and loses the null-omission the AG-UI context carries. `AGUIJsonUtilities.DefaultTypeInfoResolver` has to go in at index 0 and the M.E.AI one has to be appended after it.

Your own page won't notice, because `JSON.parse` doesn't care about extra nulls. The TypeScript AG-UI clients do, so this is a bug you only meet when you point a real client at it, which is the worst time.

### Every frame is enormous, and my `agent.iteration` is in it

Expected in 0.0.6. The adapter serialises the whole `ChatResponseUpdate` and attaches it to every event as `rawEvent`, which roughly triples the frame size and echoes anything you put in `AdditionalProperties`. There's no switch to turn it off. It's genuinely useful while you're learning the mapping, which is presumably why it's on. Strip it in your own tooling the way Step 6 does, and don't put anything you wouldn't publish into `AdditionalProperties`.

### `The WebRootPath was not found` in the startup log, and `/` returns 404

There's no `wwwroot` folder. `dotnet new web` doesn't create one. Make `src/FinanceAssistant.Web/wwwroot/` and put `index.html` in it. If the folder exists and `/` still 404s, check that `app.UseDefaultFiles()` comes *before* `app.UseStaticFiles()`.

### The page looks fine, Send just reloads it, and the browser console says `crypto.randomUUID is not a function`

You changed the address in `app.Run` so a colleague on the same wifi could see it, and you're now serving over plain `http` from a LAN address. `crypto.randomUUID` only exists in a secure context. `localhost` counts as one whatever the scheme, and `http://192.168.x.x` doesn't.

The symptom is worse than a blank page. `index.html` is static markup, so the heading, the log, the input and the button all render and look healthy. `app.js` threw on its first `crypto.randomUUID()` call, which runs at the top of the file, before it registered the submit handler. So the form falls back to a native submit, the page reloads, and nothing has happened. The `input` has no `name` attribute either, so the reload doesn't even carry your question in the query string. You land back on an empty page.

Step 4's `app.Run("http://localhost:5080")` keeps you out of this. If you do need the LAN address, add a fallback and use it in all three places `app.js` calls `crypto.randomUUID`:

```js
const newId = () =>
  crypto.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(16).slice(2)}`;
```

### `Missing AzureOpenAI:Endpoint` thrown at startup

Two candidates. The `<UserSecretsId>finance-assistant-workshop</UserSecretsId>` line is missing from `FinanceAssistant.Web.csproj`, so the builder is reading a different (empty) secret store. Or the app is not running in Development, which is when `WebApplicationBuilder` loads user secrets at all. Check `ASPNETCORE_ENVIRONMENT`.

### `Prompts/SystemPrompt.md` not found

Look in `src/FinanceAssistant.Web/bin/Debug/net10.0/`. It should contain `Prompts/SystemPrompt.md` and `scaffolding/transactions.csv`, both flowed through from the referenced project. If they're missing, the `ProjectReference` isn't there or the build didn't run. As a last resort, add the file explicitly to the web csproj:

```xml
<ItemGroup>
  <None Include="..\FinanceAssistant\Prompts\SystemPrompt.md" Link="Prompts\SystemPrompt.md">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
</ItemGroup>
```

### `This conversation is already answering something`

The per-session `SemaphoreSlim` rejected a second concurrent turn. Two requests carrying the same thread id share one session, so a question sent while another is still streaming gets this. Working as designed.

Unlike the earlier design of this exercise, an open approval card does *not* cause it. The turn ends at the interrupt and the lock is released, so a card can sit there all afternoon without blocking anything. Two browser tabs are independent too, because each generates its own thread id into its own `sessionStorage`.

### `TypedResults.ServerSentEvents` does not exist, or `System.Net.ServerSentEvents` is not found

Both are .NET 10 and neither is in the .NET 9 shared framework. This repo pins .NET 10 in `global.json`, so if the compiler can't see them, check that the csproj says `<TargetFramework>net10.0</TargetFramework>` and uses `Microsoft.NET.Sdk.Web`.

If you're porting this to .NET 9 or older, write the frames by hand. Set `Content-Type: text/event-stream` and `Cache-Control: no-cache`, then for each event write `data: {json}\n\n` to `Response.Body` followed by `await Response.Body.FlushAsync()`. AG-UI puts the event name inside the JSON rather than on an `event:` line, so there's one less thing to get right than in plain SSE. The flush is the part people forget, and it's the part that makes it stream instead of arriving in one lump at the end.

### The build breaks after upgrading `AGUI.Server`

Expected eventually. The package is at `0.0.6` and its public surface is still unshipped upstream. This exercise uses exactly three names from it: `RunAgentInput.ToChatRequestContext`, the `ChatRequestContext` type, and the `AsAGUIEventStreamAsync` extension. Check those three first. `Microsoft.Extensions.AI` supplies everything else, including `ToolApprovalRequestContent` and `ToolApprovalResponseContent`, and those are on a much slower clock.

### The console app broke after Step 2

If the `[agent]` lines vanished, check the `switch` in `RunTurnAsync`. If the reply now includes text the model wrote *before* calling a tool, the `answer.Clear()` in the `FunctionCallContent` case is missing. If the confirmation prompt stopped appearing, the `ToolApprovalRequestContent` case is not setting `pending`, so the outer `while (true)` exits after the first pass. And if the transfer prompts but the second line reads `iteration 1: final answer` where P5.01 said `iteration 2`, then `_iteration` is a local instead of a field and the resume reset it.

### `CS1061: 'ChatAgent' does not contain a definition for 'ProposeNextStepAsync'`

The replace in 2.2 took `ProposeNextStepAsync` with it. `tests/FinanceAssistant.Evals/AgentUnderTest.cs` is the caller, and that project is not in `FinanceAssistant.sln`, so `dotnet build` at the repo root will keep saying `Build succeeded` while the evals are broken. Paste the method back from the 2.2 listing.

### Memory grows the longer the server runs

Expected, and named in Step 3. Every thread id that has ever chatted has a `ChatSession` holding a full history, and nothing calls `Sweep`. Wire it to a `PeriodicTimer` in a `BackgroundService`, or accept it for a demo and restart the process.

---

## You can now

Show someone the agent without asking them to install the .NET SDK. Type a question in a browser, see which tool fired, and approve a transfer with a button instead of the word "yes". Whether the answer arrives word by word is your deployment's call, not your code's, and you now know how to tell which.

More usefully: you have a `ChatAgent` that no longer knows what a console is, and an endpoint that speaks a protocol you didn't design. It yields `ChatResponseUpdate` and pauses on approvals, and everything else about it is the loop you wrote in P3.01. The hundred and seventy lines of JavaScript in Step 5 are a demo, not a dependency. Any AG-UI client can drive this endpoint, and the next section names two you can try in an afternoon.

---

## Summary

You've added:

- **`src/FinanceAssistant.Web/`**: an ASP.NET Core project with two packages, one of them deliberately redundant, referencing the agent project for its tools, store, loop, and DI extensions.
- **`ChatAgent.RunTurnStreamingAsync`**: the P3.01 loop, now streaming through `GetStreamingResponseAsync` and `ToChatResponse()`, yielding `ChatResponseUpdate` instead of printing, and pausing on a gated call instead of reading stdin. `RunTurnAsync` folds the updates back into a string and answers the pause itself, so the console app is untouched.
- **`CloseDanglingCalls`**: the store invariant made explicit, because the interrupt model breaks it deliberately and something has to put it back.
- **`ChatSession` and `SessionRegistry`**: one conversation per AG-UI thread id, one turn at a time, with no cookie and no middleware, because the protocol carries conversation identity in the request body.
- **One endpoint and three static files**: `POST /api/chat` converting a `RunAgentInput`, running the loop, and handing the updates to `AsAGUIEventStreamAsync`, plus a page whose whole client is one `async function*` and a `switch`.

And two things you didn't write: a bespoke event record, and a second endpoint to resolve approvals.

---

## What's next

Four directions, roughly in order of how much you learn per hour spent:

1. **Point a client you didn't write at it.** This is the payoff and it takes minutes. The [AG-UI Dojo](https://dojo.ag-ui.com/) runs every protocol scenario against an endpoint you give it, and [CopilotKit](https://docs.copilotkit.ai/) is a React component library that renders chat, tool calls and approval prompts straight from these events. Your endpoint is already conformant. If the Dojo drives it, that's a stronger statement about your agent than any of the tests in this repo.

2. **Persist the conversation.** `ConversationStore` dies with the process, so a restart wipes every thread. Add a `Conversations` table keyed by thread id, serialise `ChatMessage` on append, and rehydrate in `SessionRegistry.GetOrCreate`. The store is already the single funnel for every write, which is exactly why P4.01 built it that way. Note how much closer the interrupt model puts you to this than a parked `TaskCompletionSource` did: a pending approval is now just rows in a table, so a transfer can be approved after a deploy.

3. **Put real auth in front of it.** Right now a thread id is a bearer token for somebody's conversation, and every session queries the same table and sees the same data. Key the registry on a user id from your identity provider instead, and scope the transaction queries to that user.

4. **Let the browser bring its own tools.** Step 4 throws away `context.ChatOptions.Tools`, and AG-UI put them there for a reason. A client tool is one only the client can run: read the current selection, open a file picker, ask the camera. Merge them into the session's options and the same interrupt machinery you built for approvals carries the call out to the page and the result back. It's the most interesting thing in the protocol that this exercise doesn't use.

---

## Additional Resources

- [AG-UI protocol documentation](https://docs.ag-ui.com/)
- [AG-UI .NET SDK](https://docs.ag-ui.com/sdk/dotnet/abstractions/overview), and its [source and samples](https://github.com/ag-ui-protocol/ag-ui/tree/main/sdks/dotnet)
- [Server-Sent Events in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/responses)
- [Microsoft.Extensions.AI: streaming responses](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)
- [AG-UI with Microsoft Agent Framework](https://learn.microsoft.com/en-us/agent-framework/integrations/by-component/ui/ag-ui/), the one-line route this guide deliberately didn't take
