using Factory.Core;
using Factory.Harness;
using Factory.Integration.Tests.Support;
using Factory.S2.Teams;
using Xunit;
using static Factory.Integration.Tests.Support.ScriptedChatClient;

namespace Factory.Integration.Tests.Teams;

public class AgentReviewerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("reviewer-").FullName;

    private ReviewRequest Request(IReadOnlyList<Finding>? open = null) =>
        new("run-1", "team-1", "T-001", _root, "# Task T-001: add status", "diff --git a/A.cs b/A.cs\n", 2, open ?? []);

    private static AgentReviewer Reviewer(ScriptedChatClient model, MemoryLedger ledger, Independence independence) =>
        new(new FactoryAgents(_ => model, new RoleAllowListPolicy(), ledger), independence, []);

    [Fact]
    public async Task Findings_come_from_submit_review_and_the_model_is_shown_the_diff_and_the_open_findings()
    {
        var model = new ScriptedChatClient(
            Call("submit_review", new { findings = new[] { new { category = "intent", file = "A.cs", line = 1, message = "wrong status" } } }),
            Say("done"));
        var open = new Finding(FindingCategory.Intent, "A.cs", 1, "earlier finding");

        var verdict = await Reviewer(model, new MemoryLedger(), Independence.SameFamily).ReviewAsync(Request([open]), default);

        var submitted = Assert.IsType<ReviewVerdict.Submitted>(verdict);
        Assert.Equal("wrong status", Assert.Single(submitted.Findings).Message);
        var shown = string.Join("\n", model.Requests.SelectMany(r => r).Select(m => m.Text));
        Assert.Contains("diff --git a/A.cs", shown);
        Assert.Contains("earlier finding", shown);
    }

    [Fact]
    public async Task A_reviewer_that_never_submits_is_malformed_not_an_approval()
    {
        var model = new ScriptedChatClient(Say("looks fine to me"));

        var verdict = await Reviewer(model, new MemoryLedger(), Independence.Independent).ReviewAsync(Request(), default);

        Assert.IsType<ReviewVerdict.Malformed>(verdict);
    }

    [Fact]
    public async Task The_reviewer_runs_in_its_own_role_and_cannot_write()
    {
        var ledger = new MemoryLedger();
        var model = new ScriptedChatClient(Call("write_file", new { path = "A.cs", content = "x" }), Say("done"));

        var verdict = await Reviewer(model, ledger, Independence.Independent).ReviewAsync(Request(), default);

        Assert.IsType<ReviewVerdict.Malformed>(verdict);
        Assert.False(File.Exists(Path.Combine(_root, "A.cs")));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }
}
