using Factory.Core;
using Factory.Harness;
using Factory.Integration.Tests.Support;
using Factory.S2.Teams;
using Microsoft.Extensions.AI;
using Xunit;
using static Factory.Integration.Tests.Support.ScriptedChatClient;

namespace Factory.Integration.Tests.Teams;

public class TaskRerunTests
{
    private const string NewFile = "src/Pricing.Data/QuoteStatus.cs";
    private static PlannedTask T001() => Plans.Task("T-001", Plans.Auto("file exists", $"test -f {NewFile}"));

    // 3 turns, 7 model calls: plan, test-author, implementer.
    private static ChatResponse[] Passing() =>
    [
        Say("Approach: add QuoteStatus.cs"),
        Call("declare_done", new { summary = "verify block already fails" }), Say("done"),
        Call("write_file", new { path = NewFile, content = "public enum QuoteStatus { Draft }\n" }),
        Call("declare_done", new { summary = "added QuoteStatus" }), Say("done"),
    ];

    // Never changes anything: stuck detection raises a needs-decision, so the attempt blocks.
    private static ChatResponse[] Stalling() =>
    [
        Say("Approach: think"),
        Say("still thinking"),
        Say("still thinking"),
    ];

    [Fact]
    public async Task K_fresh_attempts_report_pass_rate_turns_and_tokens()
    {
        var model = new ScriptedChatClient([.. Passing(), .. Stalling(), .. Passing()]);
        using var h = await TeamHarness.CreateAsync(model, new Limits(3, 3));

        var report = await TaskRerun.RunAsync(h.Services, h.Handover, T001(), k: 3, runIdPrefix: "rerun-test", default);

        Assert.Equal(0, model.Remaining);
        Assert.Equal("T-001", report.TaskId);
        Assert.Equal(["rerun-test-k1", "rerun-test-k2", "rerun-test-k3"], report.Attempts.Select(a => a.RunId));
        // The third attempt passes again: it starts from base, not from the first attempt's work.
        Assert.Equal(["passed", "blocked", "passed"], report.Attempts.Select(a => a.Outcome));
        Assert.Equal([3, 3, 3], report.Attempts.Select(a => a.Turns));
        Assert.Equal([600L, 300L, 600L], report.Attempts.Select(a => a.TokensIn));   // 100 per call
        Assert.Equal([120L, 60L, 120L], report.Attempts.Select(a => a.TokensOut));   // 20 per call
        Assert.Equal((2, 3), (report.Passed, report.K));
        Assert.Equal(2.0 / 3, report.PassRate, 3);
        Assert.Equal("T-001 k=3: passed 2/3 (67%), turns mean 3.0, tokens in/out mean 500/100", report.Line());
    }

    [Fact]
    public async Task Attempts_never_merge_or_open_a_pull_request()
    {
        var model = new ScriptedChatClient([.. Passing()]);
        using var h = await TeamHarness.CreateAsync(model, new Limits(3, 3));

        await TaskRerun.RunAsync(h.Services, h.Handover, T001(), k: 1, runIdPrefix: "rerun-test", default);

        Assert.DoesNotContain(h.Ledger.Events, e => e.Kind == LedgerKinds.Merge || e.Outcome == "merged_to_integration");
        Assert.False(File.Exists(Path.Combine(h.Services.Git.IntegrationPath("rerun-test-k1"), NewFile)));
    }
}
