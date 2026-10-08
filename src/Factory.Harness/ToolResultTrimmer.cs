using Microsoft.Extensions.AI;

namespace Factory.Harness;

/// Keeps a turn's request small: tool results older than the most recent few are replaced by a
/// one-line stub. The brief and the assistant's own messages are never touched, and the input list
/// is not mutated (the function-invoking client keeps the full history for the turn).
public static class ToolResultTrimmer
{
    public static IList<ChatMessage> Trim(IList<ChatMessage> messages, int keepRecent)
    {
        var total = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Count();
        var elide = total - keepRecent;
        if (elide <= 0) return messages;

        var names = messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>()
            .GroupBy(c => c.CallId).ToDictionary(g => g.Key, g => g.First().Name);

        var seen = 0;
        var result = new List<ChatMessage>(messages.Count);
        foreach (var m in messages)
        {
            if (!m.Contents.OfType<FunctionResultContent>().Any()) { result.Add(m); continue; }
            var contents = m.Contents.Select(c =>
            {
                if (c is not FunctionResultContent r || ++seen > elide) return c;
                var name = names.GetValueOrDefault(r.CallId, "tool");
                return new FunctionResultContent(r.CallId, $"[{name} result elided: {r.Result?.ToString()?.Length ?? 0} chars; call it again if you need it]");
            }).ToList();
            result.Add(new ChatMessage(m.Role, contents) { AuthorName = m.AuthorName, MessageId = m.MessageId });
        }
        return result;
    }
}

/// Applies ToolResultTrimmer to every request. Sits under function invocation, so it sees the whole
/// history of the turn on each model call.
public sealed class TrimmingChatClient(IChatClient inner, int keepRecent) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(ToolResultTrimmer.Trim(messages.ToList(), keepRecent), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(ToolResultTrimmer.Trim(messages.ToList(), keepRecent), options, cancellationToken);
}
