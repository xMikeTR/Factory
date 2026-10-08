using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Integration.Tests.Support;
using Factory.S2;
using Factory.S2.Teams;
using Microsoft.Extensions.AI;
using Xunit;
using static Factory.Integration.Tests.Support.ScriptedChatClient;

namespace Factory.Integration.Tests.Teams;

/// The review is the fourth gate: a green, stash-checked task is judged before it can pass.
public class ReviewLoopTests
{
    private static readonly IReadOnlyDictionary<string, DecisionAnswer> NoAnswers = new Dictionary<string, DecisionAnswer>();
    private const string NewFile = "src/Pricing.Data/QuoteStatus.cs";

    private static PlannedTask T001() => Plans.Task("T-001", Plans.Auto("file exists", $"test -f {NewFile}"));
    private static Finding Found(FindingCategory c, string message, int line = 1) => new(c, NewFile, line, message);

    /// plan, test author (verify block needs no new test), then `implementations` implementer turns.
    private static ScriptedChatClient Model(int implementations)
    {
        var script = new List<ChatResponse> { Say("Approach: add QuoteStatus.cs"), Call("declare_done", new { summary = "verify block already fails" }), Say("done") };
        for (var i = 0; i < implementations; i++)
        {
            script.Add(Call("write_file", new { path = NewFile, content = $"public enum QuoteStatus {{ Draft{i} }}\n" }));
            script.Add(Call("declare_done", new { summary = $"added {i}" }));
            script.Add(Say("done"));
        }
        return new ScriptedChatClient([.. script]);
    }

    private static Task<TeamResult> RunAsync(TeamHarness h, IReadOnlyDictionary<string, DecisionAnswer>? answers = null) =>
        new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(T001()), answers ?? NoAnswers, default);

    [Fact]
    public async Task A_green_task_is_reviewed_on_its_diff_before_it_passes()
    {
        var model = Model(1);
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3));

        var result = await RunAsync(h);

        Assert.Equal(TeamStatus.Passed, result.Status);
        var request = Assert.Single(h.Reviewer.Requests);
        Assert.Equal(1, request.Round);
        Assert.Empty(request.Open);
        Assert.Contains("+++ b/" + NewFile, request.Diff);
        Assert.Contains("T-001", request.Brief);
        Assert.Equal("passed", Assert.Single(h.Rows(LedgerKinds.Review)).Outcome);
    }

    [Fact]
    public async Task A_blocking_finding_sends_the_task_back_to_the_implementer_with_the_finding_then_the_fix_is_reviewed_again()
    {
        var model = Model(2);
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3));
        h.Reviewer.Then(Found(FindingCategory.Intent, "Draft0 is not a status the requirement names"));

        var result = await RunAsync(h);

        Assert.Equal(TeamStatus.Passed, result.Status);
        Assert.Equal(0, model.Remaining);
        Assert.Equal([1, 2], h.Reviewer.Requests.Select(r => r.Round));
        Assert.Equal("Draft0 is not a status the requirement names", Assert.Single(h.Reviewer.Requests[1].Open).Message);
        Assert.Contains(model.Requests.SelectMany(r => r), m => m.Text.Contains("Draft0 is not a status the requirement names"));
        Assert.Equal(["blocked", "passed"], h.Rows(LedgerKinds.Review).Select(e => e.Outcome));
    }

    [Fact]
    public async Task A_finding_that_cites_a_line_outside_the_diff_is_dropped_with_its_reason_and_does_not_block()
    {
        using var h = await TeamHarness.CreateAsync(Model(1), new Limits(8, 3));
        h.Reviewer.Then(Found(FindingCategory.Security, "injection", line: 99));

        var result = await RunAsync(h);

        Assert.Equal(TeamStatus.Passed, result.Status);
        var row = Assert.Single(h.Rows(LedgerKinds.Review));
        Assert.Equal("passed", row.Outcome);
        Assert.Contains($"{NewFile}:99 is not in the diff", row.Payload!);
    }

    [Fact]
    public async Task A_malformed_review_is_retried_once_then_parks_and_never_passes()
    {
        using var h = await TeamHarness.CreateAsync(Model(1), new Limits(8, 3));
        h.Reviewer.ThenMalformed("the reviewer never called submit_review").ThenMalformed("still no submit_review");

        var result = await RunAsync(h);

        Assert.Equal(TeamStatus.Blocked, result.Status);
        Assert.Equal("reviewer", result.Blocked!.Source);
        Assert.Equal(["A", "B", "C"], result.Blocked.Options.Select(o => o.Id));
        Assert.Equal(2, h.Reviewer.Requests.Count);
        Assert.Equal(["malformed"], h.Rows(LedgerKinds.Review).Select(e => e.Outcome));

        var accept = new Dictionary<string, DecisionAnswer> { ["T-001"] = new("T-001", "A", "reviewed by hand") };
        Assert.Equal(TeamStatus.Passed, (await RunAsync(h, accept)).Status);
        Assert.Equal(2, h.Reviewer.Requests.Count); // a designer's decision, not another review
    }

    [Fact]
    public async Task After_three_blocking_rounds_the_task_parks_for_a_designer_who_can_reject_it()
    {
        using var h = await TeamHarness.CreateAsync(Model(3), new Limits(8, 3));
        h.Reviewer.Then(Found(FindingCategory.Intent, "one")).Then(Found(FindingCategory.Intent, "two")).Then(Found(FindingCategory.Intent, "three"));

        var parked = await RunAsync(h);

        Assert.Equal(TeamStatus.Blocked, parked.Status);
        Assert.Equal("reviewer", parked.Blocked!.Source);
        Assert.Equal(["A", "B", "C"], parked.Blocked.Options.Select(o => o.Id));
        Assert.Equal(3, h.Reviewer.Requests.Count);

        var reject = new Dictionary<string, DecisionAnswer> { ["T-001"] = new("T-001", "B", "not what we meant") };
        var result = await RunAsync(h, reject);

        Assert.Equal(TeamStatus.Failed, result.Status);
        Assert.Equal(3, h.Reviewer.Requests.Count);
    }
}
