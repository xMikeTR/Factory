using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Integration.Tests.Support;
using Factory.S2;
using Xunit;

namespace Factory.Integration.Tests.Live;

/// Opt-in: FACTORY_LIVE_ANTHROPIC=1 and ANTHROPIC_API_KEY (or FACTORY_GATEWAY_KEY with
/// FACTORY_GATEWAY_URL for a gateway in front of Anthropic). Runs the fixture handover on real
/// Claude models, cheapest by default: Sonnet plans, Haiku does the rest. Local sandbox and a
/// dry-run merge, so nothing leaves the machine but the model calls.
public class LiveAnthropicTests
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("FACTORY_LIVE_ANTHROPIC") == "1";

    [Fact]
    public async Task A_real_run_ledgers_usage_tokens_and_never_the_key()
    {
        if (!Enabled) return; // opt-in
        var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "");
        env["FACTORY_PROVIDER"] = "anthropic";
        env.TryAdd("FACTORY_MODEL_FRONTIER", "claude-sonnet-5-5");
        env.TryAdd("FACTORY_MODEL_STANDARD", "claude-haiku-4-5");
        env.TryAdd("FACTORY_MODEL_SMALL", "claude-haiku-4-5");
        var gateway = GatewayOptions.FromEnvironment(env);

        using var h = await TeamHarness.CreateAsync(gateway.ChatClientFor, new Limits(12, 3));
        var result = await WorkflowHost.RunAsync(S2Workflow.Build(h.Services), new StartRun(TeamHarness.RunId, h.WriteHandoverJson()),
            new NeverMerge(), default);

        Assert.NotNull(result);
        var turns = h.Rows(LedgerKinds.Turn).ToList();
        Assert.NotEmpty(turns);
        Assert.All(turns, t => Assert.True(t.TokensIn > 0 && t.TokensOut > 0, $"{t.Stage}: no usage"));

        // The key is absent from the in-memory ledger, and from everything the run wrote to disk.
        Assert.DoesNotContain(gateway.ApiKey, string.Join('\n', h.Ledger.Events.Select(e => JsonSerializer.Serialize(e))));
        foreach (var file in Directory.EnumerateFiles(h.StateDir, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain(gateway.ApiKey, await File.ReadAllTextAsync(file));
    }

    private sealed class NeverMerge : IHumanChannel
    {
        public Task<MergeApprovalResponse> ApproveAsync(MergeApprovalRequest request, CancellationToken ct) =>
            Task.FromResult(new MergeApprovalResponse(request.RunId, false, null));
    }
}
