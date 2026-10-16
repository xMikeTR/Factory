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

    /// Adds cache_read and cache_write to a turn row's payload when the turn had any.
    public static void AddCache(JsonObject payload, UsageDetails? usage)
    {
        var (read, write) = Cache(usage);
        if (read == 0 && write == 0) return;
        payload["cache_read"] = read;
        payload["cache_write"] = write;
    }
}
