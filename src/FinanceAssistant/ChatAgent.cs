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
