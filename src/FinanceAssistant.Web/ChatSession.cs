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
