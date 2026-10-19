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

    [Fact]
    public void A_turn_that_should_have_read_the_cache_but_did_not_is_marked_cold()
    {
        var cold = new UsageDetails { InputTokenCount = 5000, AdditionalCounts = new() { ["CacheCreationInputTokens"] = 4900 } };
        var warm = new UsageDetails { InputTokenCount = 5000, CachedInputTokenCount = 4900 };

        var a = new JsonObject(); TurnUsage.AddCache(a, cold, expectWarm: true);
        Assert.True((bool)a["cold"]!);

        var b = new JsonObject(); TurnUsage.AddCache(b, warm, expectWarm: true);
        Assert.False(b.ContainsKey("cold"));

        var first = new JsonObject(); TurnUsage.AddCache(first, cold, expectWarm: false);
        Assert.False(first.ContainsKey("cold"));

        var tiny = new JsonObject(); TurnUsage.AddCache(tiny, new UsageDetails { InputTokenCount = 200 }, expectWarm: true);
        Assert.False(tiny.ContainsKey("cold"));
    }

    [Fact]
    public void Warmth_is_known_once_a_turn_read_or_wrote_the_cache()
    {
        Assert.True(TurnUsage.Touched(new UsageDetails { InputTokenCount = 9, CachedInputTokenCount = 5 }));
        Assert.False(TurnUsage.Touched(new UsageDetails { InputTokenCount = 9 }));
        Assert.False(TurnUsage.Touched(null));
    }
}
