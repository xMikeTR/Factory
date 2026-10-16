using System.Text.Json.Nodes;
using Factory.Harness;
using Microsoft.Extensions.AI;
using Xunit;

namespace Factory.Integration.Tests.Gateway;

/// A turn row's payload names the input read from and written to a prompt cache, so designc can show where input goes.
public class TurnUsageTests
{
    [Fact]
    public void Cache_reads_and_writes_go_into_the_turn_payload_only_when_there_are_any()
    {
        var payload = new JsonObject();
        TurnUsage.AddCache(payload, new UsageDetails { InputTokenCount = 160, CachedInputTokenCount = 100, AdditionalCounts = new() { ["CacheCreationInputTokens"] = 50 } });
        Assert.Equal("""{"cache_read":100,"cache_write":50}""", payload.ToJsonString());

        var none = new JsonObject();
        TurnUsage.AddCache(none, new UsageDetails { InputTokenCount = 10 });
        TurnUsage.AddCache(none, null);
        Assert.Equal("{}", none.ToJsonString());
    }

    [Fact]
    public void An_OpenAI_compatible_gateway_reports_reads_only()
    {
        Assert.Equal((30L, 0L), TurnUsage.Cache(new UsageDetails { InputTokenCount = 90, CachedInputTokenCount = 30 }));
    }
}
