using Microsoft.Extensions.AI;

namespace Factory.Integration.Tests.Support;

/// A model that plays back a fixed script, one response per request, shared across tiers.
/// Every response reports usage so tests can assert that tokens reach the ledger.
public sealed class ScriptedChatClient(params ChatResponse[] script) : IChatClient
{
    private readonly Queue<ChatResponse> _script = new(script);
    private readonly List<IReadOnlyList<ChatMessage>> _requests = [];

    public IReadOnlyList<IReadOnlyList<ChatMessage>> Requests { get { lock (_requests) return _requests.ToList(); } }
    public int Remaining { get { lock (_script) return _script.Count; } }

    public void Enqueue(params ChatResponse[] responses)
    {
        lock (_script) foreach (var r in responses) _script.Enqueue(r);
    }

    public static ChatResponse Call(string tool, object? args = null)
    {
        var dict = args is null
            ? new Dictionary<string, object?>()
            : args.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(args));
        return WithUsage(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), tool, dict)]));
    }

    public static ChatResponse Say(string text) => WithUsage(new ChatMessage(ChatRole.Assistant, text));

    private static ChatResponse WithUsage(ChatMessage m) =>
        new(m) { Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 20, TotalTokenCount = 120 } };

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        lock (_requests) _requests.Add(messages.ToList());
        lock (_script)
            return _script.TryDequeue(out var r)
                ? Task.FromResult(r)
                : throw new InvalidOperationException("scripted model ran out of responses");
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var u in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates()) yield return u;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
