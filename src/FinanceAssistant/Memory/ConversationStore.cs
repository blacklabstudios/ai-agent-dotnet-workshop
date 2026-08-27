using Microsoft.Extensions.AI;

namespace FinanceAssistant.Memory;

public class ConversationStore
{
    private readonly List<ChatMessage> _messages = new();

    public IReadOnlyList<ChatMessage> Messages => _messages;

    public void AppendSystemMessage(string text)
    {
        _messages.Add(new ChatMessage(ChatRole.System, text));
    }

    public void AppendUserMessage(string text)
    {
        _messages.Add(new ChatMessage(ChatRole.User, text));
    }

    public void AppendResponseMessages(IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            _messages.Add(message);
        }
    }

    public void AppendToolResult(AIContent content)
    {
        _messages.Add(new ChatMessage(ChatRole.Tool, [content]));
    }

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
}
