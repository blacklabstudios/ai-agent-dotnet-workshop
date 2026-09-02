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

    public async Task<string> RunTurnAsync(string input, CancellationToken ct = default)
    {
        _store.AppendUserMessage(input);

        if (_reducer is not null)
        {
            await _reducer.TryReduceAsync(_store, ct);
        }

        for (var iteration = 1; iteration <= _maxIterations; iteration++)
        {
            var response = await _chatClient.GetResponseAsync(_store.Messages, _options, ct);

            // The response carries the assistant's reply (which may include tool-call requests).
            // Append it to history so the tool-result messages we add below pair correctly with
            // the model's call requests on the next GetResponseAsync.
            _store.AppendResponseMessages(response.Messages);

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
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Tool threw something the model didn't handle (or we didn't wrap at the tool boundary).
                    // Hand the model a structured error so it can recover or apologise.
                    resultContent = new FunctionResultContent(call.CallId, $"Tool error: {ex.Message}");
                }

                _store.AppendToolResult(resultContent);
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
