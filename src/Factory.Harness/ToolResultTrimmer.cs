using Microsoft.Extensions.AI;

namespace Factory.Harness;

/// Keeps a turn's request small: tool results older than the most recent few are replaced by a
/// one-line stub. The brief and the assistant's own messages are never touched, and the input list
/// is not mutated (the function-invoking client keeps the full history for the turn). With a batch above 1,
/// results are elided `batch` at a time once `keepRecent + batch` have arrived, so the prompt before the newest
/// results stays byte-identical for a whole batch of calls: every provider's prompt cache can reuse it.
/// With `holdChars` above 0 nothing is elided while all results together fit that many characters: a stub makes the
/// agent read the file again, and that re-read costs more than holding a small result.
public static class ToolResultTrimmer
{
    public static IList<ChatMessage> Trim(IList<ChatMessage> messages, int keepRecent, int batch = 1, int holdChars = 0)
    {
        var results = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToList();
        var total = results.Count;
        if (holdChars > 0 && results.Sum(r => r.Result?.ToString()?.Length ?? 0) <= holdChars) return messages;
        var elide = total <= keepRecent ? 0 : (total - keepRecent) / batch * batch;
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
public sealed class TrimmingChatClient(IChatClient inner, int keepRecent, int batch = 1, int holdChars = 0) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(ToolResultTrimmer.Trim(messages.ToList(), keepRecent, batch, holdChars), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(ToolResultTrimmer.Trim(messages.ToList(), keepRecent, batch, holdChars), options, cancellationToken);
}
