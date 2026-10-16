using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;

namespace Factory.Harness;

/// Anthropic prompt cache breakpoints, at the two places a turn's prompt stops changing: the brief (tools,
/// instructions and the first user message come before it) and the newest elided tool result (a stub never
/// changes, and each call elides one more). Not at the end: ToolResultTrimmer rewrites the middle on every call,
/// so a breakpoint there would be written each time and never read. The input list is not mutated.
public static class PromptCache
{
    public const string ControlKey = "anthropic:cache_control";

    public static IList<ChatMessage> Mark(IList<ChatMessage> messages)
    {
        var brief = IndexOf(messages, m => m.Role == ChatRole.User && m.Contents.Count > 0 && m.Contents[^1] is TextContent);
        var stub = LastIndexOf(messages, m => m.Contents.Any(ToolResultTrimmer.IsElided));
        if (brief < 0 && stub < 0) return messages;

        var result = messages.ToList();
        if (brief >= 0) result[brief] = WithControl(result[brief], c => c is TextContent);
        if (stub >= 0) result[stub] = WithControl(result[stub], ToolResultTrimmer.IsElided);
        return result;
    }

    private static int IndexOf(IList<ChatMessage> list, Func<ChatMessage, bool> p)
    {
        for (var i = 0; i < list.Count; i++) if (p(list[i])) return i;
        return -1;
    }

    private static int LastIndexOf(IList<ChatMessage> list, Func<ChatMessage, bool> p)
    {
        for (var i = list.Count - 1; i >= 0; i--) if (p(list[i])) return i;
        return -1;
    }

    /// A copy of the message whose last content matching `which` carries the breakpoint.
    private static ChatMessage WithControl(ChatMessage m, Func<AIContent, bool> which)
    {
        var contents = m.Contents.ToList();
        var i = contents.FindLastIndex(c => which(c));
        contents[i] = contents[i] switch
        {
            TextContent t => new TextContent(t.Text) { AdditionalProperties = Control(t.AdditionalProperties) },
            FunctionResultContent r => new FunctionResultContent(r.CallId, r.Result) { Exception = r.Exception, AdditionalProperties = Control(r.AdditionalProperties) },
            var c => c,
        };
        return new ChatMessage(m.Role, contents) { AuthorName = m.AuthorName, MessageId = m.MessageId };
    }

    private static AdditionalPropertiesDictionary Control(AdditionalPropertiesDictionary? existing) =>
        new(existing ?? []) { [ControlKey] = new CacheControlEphemeral() };
}

/// Applies PromptCache to every request to an Anthropic client. Sits under TrimmingChatClient, so it sees the
/// trimmed history.
public sealed class PromptCachingChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(PromptCache.Mark(messages.ToList()), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(PromptCache.Mark(messages.ToList()), options, cancellationToken);
}
