using Factory.Core;
using Factory.Harness;
using Factory.Integration.Tests.Support;
using Factory.S2;
using Factory.S2.Teams;
using Xunit;
using static Factory.Integration.Tests.Support.ScriptedChatClient;

namespace Factory.Integration.Tests.Teams;

public class TeamRunnerTests
{
    private static readonly IReadOnlyDictionary<string, DecisionAnswer> NoAnswers = new Dictionary<string, DecisionAnswer>();
    private const string NewFile = "src/Pricing.Data/QuoteStatus.cs";

    private static PlannedTask T001() => Plans.Task("T-001", Plans.Auto("file exists", $"test -f {NewFile}"));

    [Fact]
    public async Task Plan_then_act_then_verify_passes_and_merges()
    {
        var model = new ScriptedChatClient(
            // planning turn (task-planner)
            Call("list_files", new { directory = "src/Pricing.Data" }),
            Say("Approach: add QuoteStatus.cs"),
            // acting turn (executor)
            Call("write_file", new { path = NewFile, content = "public enum QuoteStatus { Draft }\n" }),
            Call("declare_done", new { summary = "added QuoteStatus" }),
            Say("done"));
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3));

        var result = await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(T001()), NoAnswers, default);

        Assert.Equal(TeamStatus.Passed, result.Status);
        Assert.Equal(0, model.Remaining);

        Assert.Equal(3, h.Rows(LedgerKinds.ToolCall).Count(e => e.Outcome == "allowed"));
        Assert.Single(h.Rows(LedgerKinds.Configurator), e => e.Outcome == "Plan");

        var turns = h.Rows(LedgerKinds.Turn).ToList();
        Assert.Equal(2, turns.Count);
        Assert.All(turns, t => Assert.True(t.TokensIn > 0 && t.TokensOut > 0));

        Assert.Single(h.Rows(LedgerKinds.Verify), e => e.Stage == "verify");
        Assert.Equal("Passed", h.Rows(LedgerKinds.TaskEvent).Last().Outcome);

        var integration = h.Services.Git.IntegrationPath(TeamHarness.RunId);
        Assert.True(File.Exists(Path.Combine(integration, NewFile)));
        var log = await h.GitIn(integration, "log", "--format=%s");
        Assert.Contains("T-001: title", log.Output);
    }

    [Fact]
    public async Task Failing_verify_gets_one_verify_row_per_attempt_then_fails_the_team()
    {
        var never = Plans.Task("T-001", Plans.Auto("never passes", "test -f does-not-exist"));
        var model = new ScriptedChatClient(
            Say("Approach: try"),
            Call("write_file", new { path = NewFile, content = "// 1\n" }), Call("declare_done", new { summary = "1" }), Say("done"),
            Call("write_file", new { path = NewFile, content = "// 2\n" }), Call("declare_done", new { summary = "2" }), Say("done"));
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 2));

        var result = await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(never), NoAnswers, default);

        Assert.Equal(TeamStatus.Failed, result.Status);
        Assert.Equal(2, h.Rows(LedgerKinds.Verify).Count(e => e.Stage == "verify"));
        Assert.Equal("Failed", h.Rows(LedgerKinds.TaskEvent).Last().Outcome);
        // the second acting turn saw the first attempt's failure
        Assert.Contains(model.Requests.SelectMany(r => r), m => m.Text.Contains("What failed last time"));
    }

    [Fact]
    public async Task Exhausted_turn_budget_fails_without_calling_the_model_again()
    {
        var model = new ScriptedChatClient(
            Say("Approach: try"),
            Call("write_file", new { path = NewFile, content = "// 1\n" }), Say("still working"));
        using var h = await TeamHarness.CreateAsync(model, new Limits(2, 3));

        var result = await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(T001()), NoAnswers, default);

        Assert.Equal(TeamStatus.Failed, result.Status);
        Assert.Contains("TurnBudgetExhausted", result.Failure);
        Assert.Equal(0, model.Remaining);
    }

    [Fact]
    public async Task Needs_decision_blocks_and_an_answer_resumes_from_the_ledger()
    {
        var model = new ScriptedChatClient(
            Say("Approach: need a status list"),
            Call("raise_needs_decision", new { question = "Which quote statuses exist?" }), Say("asked"),
            // after the answer
            Call("write_file", new { path = NewFile, content = "public enum QuoteStatus { Draft, Bound }\n" }),
            Call("declare_done", new { summary = "added" }), Say("done"));
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3));
        var team = TeamHarness.TeamOf(T001());

        var first = await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(team, NoAnswers, default);

        Assert.Equal(TeamStatus.Blocked, first.Status);
        Assert.Equal("Which quote statuses exist?", first.Blocked!.Question);
        Assert.Equal("agent", first.Blocked.Source);

        // a fresh runner (as after a restart) replays the ledger and continues with the answer
        var answers = new Dictionary<string, DecisionAnswer> { ["T-001"] = new("T-001", "D-0002", "Draft and Bound only") };
        var second = await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(team, answers, default);

        Assert.Equal(TeamStatus.Passed, second.Status);
        Assert.Contains(model.Requests.SelectMany(r => r), m => m.Text.Contains("Draft and Bound only"));
    }
}
