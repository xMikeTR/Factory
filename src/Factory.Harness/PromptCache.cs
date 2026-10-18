using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json.Nodes;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using ChatCompletionOptions = OpenAI.Chat.ChatCompletionOptions;

namespace Factory.Harness;

/// Anthropic prompt cache breakpoints: the brief (tools, instructions and the first user message come before it,
/// and none of them changes within a turn) and the end of the request. ToolResultTrimmer elides in batches, so
/// between elisions each request extends the one before and the next call reads this one from cache; a call that
/// elides falls back to the brief. The input list is not mutated.
public static class PromptCache
{
    public const string ControlKey = "anthropic:cache_control";

    public static IList<ChatMessage> Mark(IList<ChatMessage> messages)
    {
        var brief = IndexOf(messages, m => m.Role == ChatRole.User && m.Contents.Count > 0 && m.Contents[^1] is TextContent);
        var end = LastIndexOf(messages, m => m.Contents.Any(Markable));
        if (brief < 0 && end < 0) return messages;

        var result = messages.ToList();
        if (brief >= 0) result[brief] = WithControl(result[brief], c => c is TextContent);
        if (end > brief) result[end] = WithControl(result[end], Markable);
        return result;
    }

    private static bool Markable(AIContent c) => c is TextContent or FunctionResultContent;

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
/// trimmed history. Anthropic has no cache key, so the agent's is dropped here.
public sealed class PromptCachingChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(PromptCache.Mark(messages.ToList()), PromptCacheKey.Take(options, out _), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(PromptCache.Mark(messages.ToList()), PromptCacheKey.Take(options, out _), cancellationToken);
}

/// One agent's prompt cache key, provider-neutral: `run/task/role` (the team, else `-`, when there is no task).
/// FactoryAgents puts it on every request's options; each provider's client takes it off and uses it its own way.
public static class PromptCacheKey
{
    public const string OptionKey = "factory:prompt_cache_key";

    public static string For(AgentScope s) => $"{s.RunId}/{s.TaskId ?? s.TeamId ?? "-"}/{s.Role}";

    /// A copy of the options with the key on it.
    public static ChatOptions With(ChatOptions? options, string key)
    {
        var o = options?.Clone() ?? new ChatOptions();
        o.AdditionalProperties = new(o.AdditionalProperties ?? []) { [OptionKey] = key };
        return o;
    }

    /// The options without the key (a copy when it was there), and the key.
    public static ChatOptions? Take(ChatOptions? options, out string? key)
    {
        key = options?.AdditionalProperties?.TryGetValue(OptionKey, out var v) == true ? v as string : null;
        if (key is null) return options;
        var o = options!.Clone();
        o.AdditionalProperties = new(o.AdditionalProperties!);
        o.AdditionalProperties.Remove(OptionKey);
        return o;
    }
}

/// Puts the agent's cache key on every request.
public sealed class PromptCacheKeyChatClient(IChatClient inner, string key) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(messages, PromptCacheKey.With(options, key), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(messages, PromptCacheKey.With(options, key), cancellationToken);
}

/// OpenAI-compatible gateways: the key goes on the wire as `prompt_cache_key`, so one agent's calls land on the same
/// cache (OpenAI routes by it; a gateway that does not know it ignores it). The SDK has no property for it yet, so it
/// is patched into the request body.
public sealed class OpenAIPromptCacheKeyChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(messages, Keyed(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(messages, Keyed(options), cancellationToken);

    private static ChatOptions? Keyed(ChatOptions? options)
    {
        var o = PromptCacheKey.Take(options, out var key);
        if (key is null) return o;
        var previous = o!.RawRepresentationFactory;
        o.RawRepresentationFactory = client =>
        {
            var raw = previous?.Invoke(client) as ChatCompletionOptions ?? new ChatCompletionOptions();
#pragma warning disable SCME0001 // JsonPatch is the SDK's way to send a field it does not model yet
            raw.Patch.Set("$.prompt_cache_key"u8, key);
#pragma warning restore SCME0001
            return raw;
        };
        return o;
    }
}

/// PromptCache's two breakpoints on the OpenAI-compatible wire, for a gateway that passes them to Claude: LiteLLM
/// reads `cache_control` on a user or tool message as Anthropic's on that block. The brief is the first user
/// message; the end is the last user or tool message (the last of a batch of results, so all of them are cached).
public static class OpenAICacheControl
{
    public static void Mark(JsonArray messages)
    {
        var roles = messages.Select(m => (string?)m?["role"]).ToList();
        var brief = roles.IndexOf("user");
        var end = roles.FindLastIndex(r => r is "user" or "tool");
        if (brief >= 0) Control(messages[brief]!);
        if (end > brief) Control(messages[end]!);
    }

    private static void Control(JsonNode message) => message["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
}

/// Rewrites each chat request body with OpenAICacheControl. On the wire rather than on the IChatClient messages:
/// the OpenAI adapter builds the messages (one per tool result, the instructions as a system message), and the SDK
/// has no field for the breakpoint.
public sealed class OpenAICacheControlPolicy : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Mark(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Mark(message);
        return ProcessNextAsync(message, pipeline, currentIndex);
    }

    private static void Mark(PipelineMessage message)
    {
        if (message.Request.Content is not { } content) return;
        using var buffer = new MemoryStream();
        content.WriteTo(buffer);
        if (JsonNode.Parse(buffer.ToArray()) is not JsonObject body || body["messages"] is not JsonArray messages) return;
        OpenAICacheControl.Mark(messages);
        message.Request.Content = BinaryContent.Create(BinaryData.FromString(body.ToJsonString()));
    }
}
