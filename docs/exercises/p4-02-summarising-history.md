# P4.02 - Summarising History Reducer

> Pillar 4, Part 2. Individual.

## Mission

When a conversation gets long, replace old turns with one summary message. The model keeps recent context and receives a bounded message list on every turn.

**Learning Objectives**:

- See the cost of unbounded conversation history
- Summarise old turns while keeping recent context
- Use `ConversationStore` as the one place that changes history

---

## Prerequisites

- P4.01 finished. `ConversationStore` owns every message write. `ChatAgent` routes every turn through the store. The `[memory]` line added in P4.01 Step 5 shows the count climbing turn over turn.

---

## What we're solving

Conversation history grows with every turn. The model reads it again on the next turn, so you pay for it again. Eventually you hit the context window. You will notice the token bill first.

There's a standard answer. When the conversation gets long, summarise the early turns into a single concise message and drop the originals. Keep the last few turns intact so the model still has fresh context. The compressed history goes back into the message list as a system-role "Conversation summary so far: ..." entry that lives where the old turns used to sit.

We're going to wrap that pattern in a `SummarizingHistoryReducer`. The reducer:

1. Checks if the store's message count exceeds a threshold (default 12).
2. If yes, takes everything except the system prompt and the last N tail messages (default 4).
3. Asks the model to summarise that middle section.
4. Tells the store to replace its contents with: original system prompt, the new summary as a system message, the kept tail.

The reducer hooks into `ConversationStore` through a single new method. The hook is the payoff of the design from P4.01: because every message write already goes through the store, the reducer has one chokepoint to insert at, and `Program.cs` doesn't need to know anything new.

> The summary is itself a model call. You're trading "N tokens of full history every turn forever" for "one occasional model call plus a smaller history every turn". Whether that maths out cheaper depends on turn length, message size, and how often the reducer fires. For long sessions with chatty tool results, summarisation is a clear win. For five-turn conversations that never hit the threshold, the reducer never fires and you pay nothing.

---

## If you're comfortable, do this

Use this list if you want the route first. The full steps explain the choices and help you recover when a step fails.

1. Add a `Compact(string summary, int keepTailCount)` method to `ConversationStore`. It rebuilds the message list as: original system prompt, a new system-role "Conversation summary so far: ..." message, the last N original messages. Walk the tail boundary back if it would open on a tool result, or the provider rejects the next request.
2. Create `src/FinanceAssistant/Memory/SummarizingHistoryReducer.cs`. Constructor takes `IChatClient`, threshold (default 12), keep-tail (default 4). Public method `TryReduceAsync(ConversationStore store, CancellationToken ct = default)` returns `bool`. Build the transcript from each message's `Contents`, not `.Text`, or every tool call and tool result serialises blank.
3. Update `ChatAgent` to accept an optional `SummarizingHistoryReducer` and call its `TryReduceAsync` right after appending the user message, before the loop.
4. In `Program.cs`, instantiate the reducer and pass it into `ChatAgent`.
5. Run a long conversation (12+ turns of small talk). Watch the `[memory]` line drop after the reducer fires.

---

## Step 1: Add Compact to ConversationStore

Open `src/FinanceAssistant/Memory/ConversationStore.cs`. Add this method below the existing `Append*` methods:

```csharp
public void Compact(string summary, int keepTailCount)
{
    if (_messages.Count == 0)
    {
        return;
    }

    var systemMessage = _messages[0];

    var start = Math.Max(1, _messages.Count - keepTailCount);

    // Never open the tail on a tool result. Its tool call sits earlier and is about to be
    // summarised away, and providers reject a tool message with no tool call before it.
    // Walk back until the tail starts on the call, so the pair travels together.
    while (start > 1 && start < _messages.Count &&
           _messages[start].Contents.Any(c => c is FunctionResultContent))
    {
        start--;
    }

    var tail = _messages.Skip(start).ToList();

    var summaryMessage = new ChatMessage(
        ChatRole.System,
        $"Conversation summary so far: {summary}");

    _messages.Clear();
    _messages.Add(systemMessage);
    _messages.Add(summaryMessage);
    _messages.AddRange(tail);
}
```

Two things worth noticing:

**The original system prompt stays at index 0.** Without it, the model loses the agent's persona and behaviour rules. The compaction never touches that message, regardless of how long the conversation has run.

**The summary goes in as a system role.** Not as an assistant message (because the assistant didn't say it), not as a user message (because the user didn't either). System is the right channel for "background context you should keep in mind". Some teams use a custom role or a tagged user message. System is the simplest option and works with every provider.

> `Compact` is the first method on the store that does anything to the list other than append to it. It's still the only path into the list. The reducer doesn't reach in directly. It calls `Compact`.

> Every boundary check in the tail slice is a guard, not a calculation. `Math.Max(1, ...)` handles the top: if the conversation is shorter than `keepTailCount`, the naive `Skip(_messages.Count - keepTailCount)` would walk past index 0 and grab the system prompt as part of the tail, so flooring the skip at 1 keeps the system message from ending up duplicated. The `while` loop handles the other end of the same problem. A tool result only means something next to the tool call that asked for it, and every provider enforces that, so if the tail would open on a tool result the loop walks the boundary back until it opens on the call instead.

> The `start < _messages.Count` term inside the `while` is the third guard, and it only matters at `keepTailCount: 0`. There `start` lands one past the last message, so reading `_messages[start]` to check its contents throws `ArgumentOutOfRangeException` before the loop ever gets to think about tool results. With the bound in place, `keepTailCount: 0` does the sensible thing instead: no tail, summary only.

> Walking the boundary **back** rather than forward puts one message in two places: the assistant tool-call message gets summarised into the head *and* kept verbatim in the tail. That's the cheaper of the two trades. Walking forward would skip the orphaned result out of the tail, but the head was already sliced before the boundary moved, so that result would fall out of the summary too and simply vanish. A duplicated tool call costs a few tokens. A lost tool result costs the answer.

---

## Step 2: Create SummarizingHistoryReducer.cs

Create `src/FinanceAssistant/Memory/SummarizingHistoryReducer.cs`:

```csharp
using Microsoft.Extensions.AI;

namespace FinanceAssistant.Memory;

public class SummarizingHistoryReducer
{
    private readonly IChatClient _chatClient;
    private readonly int _threshold;
    private readonly int _keepTailCount;

    public SummarizingHistoryReducer(IChatClient chatClient, int threshold = 12, int keepTailCount = 4)
    {
        _chatClient = chatClient;
        _threshold = threshold;
        _keepTailCount = keepTailCount;
    }

    public async Task<bool> TryReduceAsync(ConversationStore store, CancellationToken ct = default)
    {
        if (store.Messages.Count <= _threshold)
        {
            return false;
        }

        // The head we're about to summarise: everything except the system prompt and the kept tail.
        var headCount = store.Messages.Count - _keepTailCount - 1;
        var headToSummarise = store.Messages
            .Skip(1)
            .Take(headCount)
            .ToList();

        if (headToSummarise.Count == 0)
        {
            return false;
        }

        var transcript = string.Join(
            "\n",
            headToSummarise.Select(m =>
            {
                var content = string.Join(" | ", m.Contents.Select(c => c switch
                {
                    TextContent t => t.Text,
                    FunctionCallContent fc => $"[tool call: {fc.Name}({string.Join(", ", fc.Arguments?.Select(a => $"{a.Key}={a.Value}") ?? [])})]",
                    FunctionResultContent fr => $"[tool result: {fr.Result}]",
                    _ => c.ToString()
                }));
                return $"[{m.Role}] {content}";
            }));

        var summaryRequest = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a conversation summariser. Output only the summary, no preamble. Preserve key facts, user preferences, decisions, numbers, and dates. Skip pleasantries."),
            new(ChatRole.User, $"Summarise this conversation in 2 to 4 sentences:\n\n{transcript}")
        };

        var response = await _chatClient.GetResponseAsync(summaryRequest, cancellationToken: ct);
        var summary = string.IsNullOrWhiteSpace(response.Text)
            ? "(summary unavailable)"
            : response.Text.Trim();

        Console.WriteLine($"[memory] reducing {headToSummarise.Count} messages into 1 summary");
        store.Compact(summary, _keepTailCount);
        return true;
    }
}
```

Six things worth reading carefully:

**The transcript walks `Contents`, not `.Text`.** `ChatMessage.Text` only concatenates the `TextContent` items in a message. An assistant message that carries a tool-call request has none, and neither does a tool message carrying the result, so both come back as an empty string. Build the transcript from `.Text` and you hand the summariser a conversation with holes in exactly the places the facts were: which tool the agent chose, what arguments it passed, and what came back. The `switch` renders each content type instead, so `[tool call: Convert(from=USD, to=EUR)]` and `[tool result: 21.21 EUR]` survive into the summary. The `_` arm is the catch-all for the content types this workshop never produces.

**The empty-head guard catches a misconfigured reducer, nothing else.** With any sane pair of values (`threshold` comfortably above `keepTailCount`) `headCount` is always positive by the time the threshold check has passed, so `headToSummarise.Count == 0` never fires. It's there for the case where someone sets `threshold: 4, keepTailCount: 6`: `headCount` goes negative, `Take` returns an empty sequence rather than throwing, and without the guard you'd pay for a model call that summarises nothing and then compact the list on the result.

**The threshold and keep-tail are constructor parameters.** Defaults sit at 12 and 4 because that gives a single user-assistant-tool round of context in the tail (system prompt + summary + 4 recent messages = ~6 messages going to the model after a reduction). Tune both for your domain. Long technical discussions want a bigger tail. Quick lookups can survive with 2.

**The summariser is the same `gpt-5.6-luna` we use for everything else.** It doesn't have to be. Many production systems use a smaller, cheaper model for summarisation specifically, because the task is well-bounded and the model doing the actual reasoning is overkill for it.

**The fallback checks for whitespace, not null.** `ChatResponse.Text` is a non-nullable `string`, so `response.Text ?? "(summary unavailable)"` would be a fallback that can never fire. What the model actually returns on a bad day is an empty or whitespace-only string, which is why the guard is `string.IsNullOrWhiteSpace`. Compacting on a blank summary is worse than not compacting at all: you throw away the head and put nothing in its place.

**The summariser prompt is doing real work.** "Preserve key facts, user preferences, decisions, numbers, and dates. Skip pleasantries." is the instruction that decides what survives. Drop "numbers and dates" from that list and the agent will forget the totals from earlier tool calls, leaving the user confused on the next turn. Drop "user preferences" and a stated preference from turn three is gone by turn fifteen. Test the prompt on actual transcripts before you ship.

---

## Step 3: Wire the reducer into ChatAgent

Two small edits in `src/FinanceAssistant/ChatAgent.cs`.

### 3.1: Add an optional reducer to the constructor

Add a `_reducer` field and accept it as an optional constructor parameter:

```csharp
private readonly SummarizingHistoryReducer? _reducer;

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
    _maxIterations = maxIterations;
    _reducer = reducer;
    _store.AppendSystemMessage(systemPrompt); // existing line from P4.01, stays
}
```

The reducer is nullable on purpose. An agent without a reducer is still a valid agent. The store just grows unbounded, which is fine for short sessions or test runs.

> `reducer` goes **before** `maxIterations` in the parameter list, so `Program.cs` can pass it positionally in Step 4 without naming it. That does change the positional signature. Nothing in the workshop passes `maxIterations` positionally, so no call site breaks. If you customised the iteration cap back in P3.01 and passed it as the fifth argument, name it (`maxIterations: 12`) or you'll get a type error here.

### 3.2: Call the reducer once per turn

At the top of `RunTurnAsync`, after appending the user message, before the iteration loop:

```csharp
public async Task<string> RunTurnAsync(string input, CancellationToken ct = default)
{
    _store.AppendUserMessage(input);

    if (_reducer is not null)
    {
        await _reducer.TryReduceAsync(_store, ct);
    }

    for (var iteration = 1; iteration <= _maxIterations; iteration++)
    {
        // ...existing loop body unchanged
    }
}
```

> The reducer runs **after** appending the user message and **before** the model call. That ordering matters. The new user message is part of "the tail we want to keep", not part of "the head we want to summarise". Reduce before appending the user message and you risk summarising a question the model hasn't answered yet.

> The reducer runs **once per turn**, not once per iteration. A single turn that burns several tool-calling iterations can add ten or more messages before the next reduction gets a chance, which is why you'll sometimes see `[memory]` jump by four or more between prompts without a `reducing` line in between. The `maxIterations` cap from P3.01 bounds how far that can run, so the list is still bounded, just not tightly within a turn. Moving the call inside the loop would fix that and cost you a threshold check plus a possible summariser call on every iteration, which is a bad trade.

> `TryReduceAsync` returns a `bool` that we discard here. That's fine. The return value is there in case you want to log "reduction happened on this turn" or branch on it (e.g., only persist the conversation to disk after a reduction). The agent's behaviour is the same whether it fires or not, so the call site doesn't need to read the result.

---

## Step 4: Pass the reducer in from Program.cs

In `Program.cs`, find the line that constructs the `ChatAgent`:

```csharp
var chatAgent = new ChatAgent(chatClient, chatOptions, store, systemPrompt);
```

Replace it with the two-line version that builds the reducer first:

```csharp
var reducer = new SummarizingHistoryReducer(chatClient);
var chatAgent = new ChatAgent(chatClient, chatOptions, store, systemPrompt, reducer);
```

That's the entire `Program.cs` change. No other lines move.

---

## Step 5: Force a reduction and watch it fire

The `[memory] {store.Messages.Count} messages in history` line you added in P4.01 Step 5 is still in `Program.cs`, inside the `while` loop just before the `RunTurnAsync` call. Nothing in this exercise touches it. It's the easiest way to see the reducer working, so leave it where it is.

Run the project:

```bash
dotnet run --project src/FinanceAssistant
```

Hold a long conversation. The fastest way to push past the threshold is small back-and-forth turns:

```
> hi
> what tools do you have?
> what is gpt-5.6-luna good for?
> can you list a few things you can help me with?
> what are some common categories in personal finance?
> ...
```

After every turn, the `[memory]` line shows the count climbing. Around turn six (sooner if the agent calls tools, since tool-call requests and tool results both land as messages) you'll cross the threshold of 12 and see:

```
[memory] 13 messages in history
[memory] reducing 9 messages into 1 summary
```

The `[memory]` line prints before the user message is appended, so the reducer actually sees 14 and summarises 14 - 4 - 1 = 9. `Compact` leaves six messages behind: the system prompt, the summary, and the last four (the newest of which is the question you just typed).

What the next `[memory]` line reads depends on what the turn did after that. A plain question adds one assistant reply, so you'll see seven. A turn that calls a tool adds the tool-call message and its result too, so nine is just as normal. Either way the number restarts from six rather than climbing from thirteen, and that restart is the whole point.

Now ask a question that depends on a **fact** from one of the summarised turns. Something you told the agent works best: state a budget limit early ("my budget limit is 1200 EUR per month"), then ask for it back after the reduction fires. Or ask about a tool result the agent looked up ("what was that transaction amount again?"). The model can still answer if the summary captured it. If it can't, the summariser prompt needs sharpening.

Resist testing this with "what did I ask you first?". The summariser prompt tells the model to skip pleasantries and compress to a few sentences, so who-said-what-in-which-order is precisely the thing it's instructed to throw away. A wrong answer there means the prompt is working, not failing, and you'll waste ten minutes sharpening a prompt that has nothing wrong with it. Test for facts, not for transcript positions.

### Watch it fire more than once

With the defaults, `Compact` drops the count to six, so you need six more messages before the threshold bites again. That's three more turns of small talk, and in a workshop slot you may only ever see one reduction.

To watch the cycle repeat, drop the thresholds in `Program.cs`:

```csharp
var reducer = new SummarizingHistoryReducer(chatClient, threshold: 6, keepTailCount: 2);
```

Now every second or third turn fires a reduction, and you can see something the single-fire run hides: after the first reduction, index 1 is a summary. The next reduction sweeps that summary into its own head slice, so what you get is a summary of a summary. Facts survive one round easily and get vaguer with each one after. That drift is the real cost of the pattern, and it's the reason the "test the prompt on actual transcripts" advice in Step 2 says *transcripts*, plural, over a long session. Put the defaults back to 12 and 4 when you've seen it.

---

## What's actually happening inside one reducing turn

The `[memory]` line only prints between turns, so the trace hides what the reducer is doing mid-turn. Here's a full walk-through of one turn that crosses the threshold, using realistic numbers from a tool-using conversation.

**Starting state.** Previous turn ended with 15 messages in the store, so the next prompt shows `[memory] 15`. The list looks like this:

```
index │ role       │ content (abbreviated)
──────┼────────────┼────────────────────────────────────────────
  0   │ system     │ "You are a finance assistant..."
  1   │ user       │ "hello"
  2   │ assistant  │ "Hello! How can I assist..."
  3   │ user       │ "how are you?"
  4   │ assistant  │ "I'm just a program..."
  5   │ user       │ "What's the most recent transaction?"
  6   │ assistant  │ <FunctionCall GetTransactions>
  7   │ tool       │ <FunctionResult: REWE 2024-06-28 -23.33>
  8   │ assistant  │ "The most recent transaction was..."
  9   │ user       │ "Do you know the currency?"
 10   │ assistant  │ "The currency hasn't been specified..."
 11   │ user       │ "assume it's USD. Show me in eur."
 12   │ assistant  │ <FunctionCall Convert>
 13   │ tool       │ <FunctionResult: 21.21 EUR>
 14   │ assistant  │ "Assuming USD, ~21.21 EUR."
```

**The user types `"Now in cad"`. Inside `RunTurnAsync`:**

**Step 1: append user message.** Count goes 15 → 16. (You never see 16 in the trace because `[memory]` prints before this happens.)

```
 15   │ user       │ "Now in cad"          ← new
```

**Step 2: reducer runs (`16 > threshold 12`).** It slices the list into three regions:

```
                   ┌─────────────────────────────────────┐
   keep at top ──► │  0  system  (the original prompt)   │
                   ├─────────────────────────────────────┤
                   │  1  user       "hello"              │
                   │  2  assistant  "Hello!..."          │
                   │  3  user       "how are you?"       │
   summarise  ──►  │  4  assistant  "I'm just..."        │  11 messages
   into one        │  5  user       "most recent tx?"    │  →  1 summary
   summary         │  6  assistant  <call GetTx>         │
                   │  7  tool       <REWE 2024-06-28>    │
                   │  8  assistant  "most recent was..." │
                   │  9  user       "know the currency?" │
                   │ 10  assistant  "not specified..."   │
                   │ 11  user       "USD. Show me in eur"│
                   ├─────────────────────────────────────┤
                   │ 12  assistant  <call Convert>       │
   keep tail  ──►  │ 13  tool       <21.21 EUR>          │  keepTail = 4
   (last 4)        │ 14  assistant  "Assuming USD..."    │
                   │ 15  user       "Now in cad"         │
                   └─────────────────────────────────────┘
```

The summariser model is called on the middle slice and returns a two to four sentence summary.

Messages 6 and 7 are the pair the naive transcript used to lose. They reach the summariser as `[assistant] [tool call: GetTransactions(...)]` and `[tool] [tool result: REWE 2024-06-28 -23.33]`, which is why the summary in Step 3 can quote a merchant and an amount the user never typed. Build that transcript from `.Text` and both lines arrive blank, the summary has nothing to carry forward, and the agent forgets the transaction it just looked up.

**Step 3: `Compact` rebuilds the list. Count: 16 → 6.**

```
index │ role       │ content
──────┼────────────┼────────────────────────────────────────────
  0   │ system     │ "You are a finance assistant..."    ← preserved
  1   │ system     │ "Conversation summary so far: user  ← NEW
      │            │  greeted, asked for most recent     │ (summary)
      │            │  transaction (REWE -23.33 on 2024-  │
      │            │  06-28). User said assume USD..."   │
  2   │ assistant  │ <FunctionCall Convert>              ← tail
  3   │ tool       │ <FunctionResult: 21.21 EUR>         ← tail
  4   │ assistant  │ "Assuming USD, ~21.21 EUR."         ← tail
  5   │ user       │ "Now in cad"                        ← tail
```

The tail opened on index 12, the assistant message carrying the `Convert` call, so its result at index 13 travelled with it and the boundary guard in `Compact` had nothing to do. That's the shape of the conversation, not a property of the design. Shift everything by one message and the boundary lands on the tool result instead, with its call stranded in the summarised head. The guard walks the boundary back one position in that case, which is what keeps the rebuilt list a list the provider will accept.

**Step 4: the agent loop runs against this 6-message context.** The model answers, calling `Convert` once:

```
  6   │ assistant  │ <FunctionCall Convert USD→CAD>     +1
  7   │ tool       │ <FunctionResult: 31.53 CAD>        +1
  8   │ assistant  │ "~31.53 CAD."                       +1
```

End of turn: **9 messages**. The next turn opens with `[memory] 9`.

### Two things worth noticing

**The peak (16) and trough (6) never appear in the trace.** You see 15 → 9 across the turn, which understates the work. The hidden 16 → 6 reduction is where the win actually happens. If you want to see those numbers directly, move the log into `RunTurnAsync` and print it twice: once after `AppendUserMessage`, once after the reducer call.

**There are now two `system` messages.** Index 0 is the persona prompt. Index 1 is the summary. That's why `Compact` is careful to preserve `_messages[0]` before rebuilding: if it overwrote index 0 with the summary, the agent would lose every behaviour rule the system prompt set, and the model on the next turn would have no idea it's a finance assistant.

### Without the reducer

The same "Now in cad" turn would have ended at **19 messages**, and the next turn would carry all 19 forward into the prompt, then 23, then 27: token cost growing linearly forever. Across a 30-turn session that's roughly 1,000 message-positions sent to the model, against a bounded ~30 × 9 ≈ 270 with the reducer, plus one extra summariser call per reduction. The longer the session, the bigger the gap, because the unreduced side is a triangle and the reduced side is a rectangle.

---

## Troubleshooting

### `[memory] reducing` never fires

The conversation isn't long enough. Threshold defaults to 12 messages, but every user turn adds at least two (user + assistant) and tool-calling turns add more. Keep going for a few more turns. If you want it to fire faster, lower the threshold in the constructor: `new SummarizingHistoryReducer(chatClient, threshold: 6, keepTailCount: 2)`.

### Reducer fires but the agent forgets recent context

You set `keepTailCount` too low. At `keepTailCount: 0` the tail is gone entirely and the model sees nothing but the system prompt and the summary, so anything the summariser compressed away is unrecoverable. At 1 or 2 you keep a sliver, which is enough for a quick-lookup agent and not enough for a conversation. Bump it back to 4 or higher.

If you get an `ArgumentOutOfRangeException` out of `Compact` instead of a forgetful agent, the `start < _messages.Count` term is missing from the `while` condition in Step 1. Without it, `keepTailCount: 0` reads one past the end of the list.

### Provider rejects the request right after a reduction

A 400 worded close to *messages with role 'tool' must be a response to a preceding message with 'tool_calls'*. The tell is the timing: it fires on the first model call after a `[memory] reducing ...` line and never before.

The rebuilt list is opening its tail on a tool result whose tool-call message went into the summary. Check that the `while` loop from Step 1 is in `Compact`. Without it, the fixed last-N slice lands mid-pair whenever the message count works out that way, which makes it look intermittent: the same code survives ten reductions and fails on the eleventh.

### Summary is garbage

Either the summariser prompt is too weak, or the model cannot compress the history. Add concrete instructions: "Preserve transaction totals, category names, and date ranges. Skip greetings and acknowledgements." If tool-call JSON dominates the conversation, the summariser sees noisy text. Shorten the `[tool]` result shape.

Blank lines in the transcript are a separate problem, and after Step 2 they are a real signal rather than a normal one. A message carrying a tool call should render as `[assistant] [tool call: Convert(from=USD, to=EUR)]`, and its result as `[tool] [tool result: 21.21 EUR]`. A bare `[assistant]` with nothing after the bracket means the `Contents` switch didn't land and the transcript is still being built from `m.Text`, so the summariser is being asked to compress a conversation with the numbers cut out of it.

### Agent loses persona after reduction

The system prompt at index 0 isn't being preserved. Check that `ConversationStore.Compact` keeps `_messages[0]` before clearing and rebuilding. If you accidentally replaced it with the summary, the model loses every behaviour rule the system prompt set.

### Compile error on `_reducer` access

`ChatAgent.RunTurnAsync` is calling `_reducer.TryReduceAsync` without the null check. Wrap it: `if (_reducer is not null) { await _reducer.TryReduceAsync(_store, ct); }`. The reducer is optional on purpose.

---

## You can now

Hold a conversation as long as you want. The reducer keeps the message list bounded. The agent still has the system prompt, a summary of what came before, and the recent context to reason over.

---

## Summary

You've added:

- **`ConversationStore.Compact(string summary, int keepTailCount)`**: rebuilds the message list as [system prompt, summary as system message, kept tail], with a boundary guard so the tail never opens on a tool result whose call has just been summarised away.
- **`Memory/SummarizingHistoryReducer.cs`**: checks the threshold, renders the head as a transcript that keeps tool calls and their results, summarises it via a model call, calls `store.Compact` unless the summary came back blank. Threshold and tail size are configurable. Defaults are 12 and 4.
- **`ChatAgent` updated**: takes an optional `SummarizingHistoryReducer`. Calls `TryReduceAsync` right after appending the user message, before the loop.
- **`Program.cs` updated**: constructs the reducer and passes it into the agent.
- **A bounded conversation**: long sessions stay token-cheap and stay inside the context window.

---

## What's next

P5.01 wraps a destructive tool in `ApprovalRequiredAIFunction` and enforces the gate inside the loop you just extended, so the agent has to ask the human before doing something irreversible.

---

## Additional Resources

- [Microsoft.Extensions.AI: chat messages and roles](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)
- [Anthropic: long-context patterns and conversation management](https://www.anthropic.com/engineering/building-effective-agents)
