using FinanceAssistant.Memory;
using Microsoft.Extensions.AI;

namespace FinanceAssistant;

public class ChatAgent
{
    private readonly IChatClient _chatClient;
    private readonly ChatOptions _options;
    private readonly ConversationStore _store;
    private readonly int _maxIterations;
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

        _store.AppendSystemMessage(systemPrompt);
    }

    // One model call against the conversation as this agent assembles it, stopping before any
    // tool runs. That first response already carries the behaviour worth grading cheaply: what
    // the agent decided to say and do. RunTurnAsync starts with this exact call, so an eval
    // that stops here and the REPL that carries on cannot disagree about how a turn was built.
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
        var response = await ProposeNextStepAsync(input, ct);

        for (var iteration = 1; iteration <= _maxIterations; iteration++)
        {
            // The response carries the assistant's reply (which may include tool-call requests).
            // Append it to history so the tool-result messages we add below pair correctly with
            // the model's call requests on the next GetResponseAsync.
            _store.AppendResponseMessages(response.Messages);

            // The model is done if it didn't ask for tool calls.
            if (response.FinishReason != ChatFinishReason.ToolCalls)
            {
                Console.WriteLine($"[agent] iteration {iteration}: final answer");
                return response.Text;
            }

            // Otherwise, find every FunctionCallContent in the response and invoke the matching tool.
            var toolCalls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .ToList();

            var toolNames = string.Join(", ", toolCalls.Select(c => c.Name));
            Console.WriteLine($"[agent] iteration {iteration}: calling {toolNames}");

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

            // Ask again, now that the tool results are in history. The cap counts model calls,
            // and the first one was made by ProposeNextStepAsync above, so the last iteration
            // does not make a call it would only throw away.
            if (iteration < _maxIterations)
            {
                response = await _chatClient.GetResponseAsync(_store.Messages, _options, ct);
            }
        }

        // Hit the iteration cap. The model is probably stuck in a tool-call loop.
        // Return whatever the last assistant text was, or a fallback message.
        Console.Error.WriteLine($"[agent] iteration cap of {_maxIterations} hit. The agent looped on tool calls without producing a final answer.");
        var lastAssistantText = _store.Messages
            .Where(m => m.Role == ChatRole.Assistant)
            .Select(m => m.Text)
            .LastOrDefault(t => !string.IsNullOrWhiteSpace(t));
        return lastAssistantText ?? "(no final answer, iteration cap reached)";
    }

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
}
