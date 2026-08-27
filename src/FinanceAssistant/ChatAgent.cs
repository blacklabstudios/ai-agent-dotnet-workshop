using FinanceAssistant.Memory;
using Microsoft.Extensions.AI;

namespace FinanceAssistant;

public class ChatAgent
{
    private readonly IChatClient _chatClient;
    private readonly ChatOptions _options;
    private readonly ConversationStore _store;
    private readonly int _maxIterations;

    public ChatAgent(IChatClient chatClient, ChatOptions options, ConversationStore store, string systemPrompt, int maxIterations = 8)
    {
        _chatClient = chatClient;
        _options = options;
        _store = store;
        _maxIterations = maxIterations;

        _store.AppendSystemMessage(systemPrompt);
    }

    public async Task<string> RunTurnAsync(string input, CancellationToken ct = default)
    {
        _store.AppendUserMessage(input);

        for (var iteration = 1; iteration <= _maxIterations; iteration++)
        {
            var response = await _chatClient.GetResponseAsync(_store.Messages, _options, ct);

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
                    catch (Exception ex)
                    {
                        // Tool threw something the model didn't handle (or we didn't wrap at the tool boundary).
                        // Hand the model a structured error so it can recover or apologise.
                        resultContent = new FunctionResultContent(call.CallId, $"Tool error: {ex.Message}");
                    }
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
}
