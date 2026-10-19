using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Factory.Harness;

/// The part of a turn's input that a prompt cache served (read) or stored (write). InputTokenCount already
/// counts both, as designc's tokens_in does. Anthropic reports writes as an additional count; OpenAI-compatible
/// gateways report reads only.
public static class TurnUsage
{
    public const string CacheWriteCount = "CacheCreationInputTokens";

    public static (long Read, long Write) Cache(UsageDetails? usage) =>
        (usage?.CachedInputTokenCount ?? 0,
         usage?.AdditionalCounts is { } extra && extra.TryGetValue(CacheWriteCount, out var w) ? w : 0);

    /// Below this much input a provider does not cache at all, so a miss means nothing.
    public const long MinCacheable = 1024;

    /// True once a turn read or wrote the cache: the next turn of the same agent should read it.
    public static bool Touched(UsageDetails? usage) => Cache(usage) is (> 0, _) or (_, > 0);

    /// Adds cache_read and cache_write to a turn row's payload when the turn had any. With `expectWarm` (an
    /// earlier turn of this agent touched the cache) a turn that read nothing from a cacheable prompt is marked
    /// `cold`: the prefix expired or changed, and the whole conversation was paid for again.
    public static void AddCache(JsonObject payload, UsageDetails? usage, bool expectWarm = false)
    {
        var (read, write) = Cache(usage);
        if (read == 0 && write == 0) return;
        payload["cache_read"] = read;
        payload["cache_write"] = write;
        if (expectWarm && read == 0 && (usage?.InputTokenCount ?? 0) >= MinCacheable) payload["cold"] = true;
    }
}
