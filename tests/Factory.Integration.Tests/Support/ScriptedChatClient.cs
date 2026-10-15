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

    /// The planner's submit_plan call: the plan JSON's top-level fields become the tool arguments.
    public static ChatResponse SubmitPlan(string planJson)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(planJson);
        var dict = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        return WithUsage(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), "submit_plan", dict)]));
    }

    /// A step that simulates the process being killed: cancels the run and throws, mid-turn.
    public static ChatResponse Kill(CancellationTokenSource run) => new(new ChatMessage(ChatRole.Assistant, "<kill>")) { AdditionalProperties = new() { ["kill"] = run } };

    /// A step that does something to the disk first, as a build the agent ran would (touching obj/, say).
    public static ChatResponse After(ChatResponse r, Action effect)
    {
        (r.AdditionalProperties ??= new())["effect"] = effect;
        return r;
    }

    public static ChatResponse Say(string text) => WithUsage(new ChatMessage(ChatRole.Assistant, text));

    private static ChatResponse WithUsage(ChatMessage m) =>
        new(m) { Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 20, TotalTokenCount = 120 } };

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        lock (_requests) _requests.Add(messages.ToList());
        lock (_script)
        {
            if (!_script.TryDequeue(out var r)) throw new InvalidOperationException("scripted model ran out of responses");
            if (r.AdditionalProperties?.TryGetValue("kill", out var k) == true && k is CancellationTokenSource run)
            {
                run.Cancel();
                throw new OperationCanceledException(run.Token);
            }
            if (r.AdditionalProperties?.TryGetValue("effect", out var f) == true && f is Action effect) effect();
            return Task.FromResult(r);
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var u in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates()) yield return u;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
