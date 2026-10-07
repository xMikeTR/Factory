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
    private const string TestFile = "tests/Pricing.Data.Tests/status.sh";

    private static PlannedTask T001() => Plans.Task("T-001", Plans.Auto("file exists", $"test -f {NewFile}"));

    [Fact]
    public async Task Plan_then_act_then_verify_passes_and_merges()
    {
        var model = new ScriptedChatClient(
            // planning turn (task-planner)
            Call("list_files", new { directory = "src/Pricing.Data" }),
            Say("Approach: add QuoteStatus.cs"),
            // test-author turn: the verify block needs no new test file
            Call("declare_done", new { summary = "verify block already fails" }),
            Say("done"),
            // implementer turn
            Call("write_file", new { path = NewFile, content = "public enum QuoteStatus { Draft }\n" }),
            Call("declare_done", new { summary = "added QuoteStatus" }),
            Say("done"));
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3));

        var result = await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(T001()), NoAnswers, default);

        Assert.Equal(TeamStatus.Passed, result.Status);
        Assert.Equal(0, model.Remaining);

        Assert.Equal(4, h.Rows(LedgerKinds.ToolCall).Count(e => e.Outcome == "allowed"));
        Assert.Single(h.Rows(LedgerKinds.Configurator), e => e.Outcome == "Plan");
        Assert.All(h.Rows(LedgerKinds.Configurator), e =>
            Assert.Equal("heuristic", System.Text.Json.JsonDocument.Parse(e.Payload!).RootElement.GetProperty("decider").GetString()));

        var turns = h.Rows(LedgerKinds.Turn).ToList();
        Assert.Equal(3, turns.Count);
        Assert.All(turns, t => Assert.True(t.TokensIn > 0 && t.TokensOut > 0));

        Assert.Single(h.Rows(LedgerKinds.Verify), e => e.Stage == "verify");
        Assert.Equal("Passed", h.Rows(LedgerKinds.TaskEvent).Last().Outcome);

        var integration = h.Services.Git.IntegrationPath(TeamHarness.RunId);
        Assert.True(File.Exists(Path.Combine(integration, NewFile)));
        var log = await h.GitIn(integration, "log", "--format=%s");
        Assert.Contains("T-001: title", log.Output);
    }

    /// A check that generates files (p compile, test logs) under testWrites: they are output,
    /// not tests, and must reach neither the tests commit nor the task's commit.
    [Fact]
    public async Task Files_a_check_generates_are_never_committed()
    {
        const string generated = "tests/Pricing.Data.Tests/P/PGenerated/Gen.cs";
        var task = Plans.Task("T-001", Plans.Auto("generates then checks",
            $"mkdir -p tests/Pricing.Data.Tests/P/PGenerated && echo gen > {generated} && test -f {NewFile}"));
        var model = new ScriptedChatClient(
            Say("Approach: add QuoteStatus.cs"),
            Call("declare_done", new { summary = "verify block already fails" }), Say("done"),
            Call("write_file", new { path = NewFile, content = "public enum QuoteStatus { Draft }\n" }),
            Call("declare_done", new { summary = "added QuoteStatus" }), Say("done"));
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3));

        var result = await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(task), NoAnswers, default);

        Assert.Equal(TeamStatus.Passed, result.Status);
        var integration = h.Services.Git.IntegrationPath(TeamHarness.RunId);
        Assert.True(File.Exists(Path.Combine(integration, NewFile)));
        var everCommitted = await h.GitIn(integration, "log", "--all", "--name-only", "--format=");
        Assert.DoesNotContain(generated, everCommitted.Output);
    }

    [Fact]
    public async Task Files_the_baseline_suite_generates_are_never_committed()
    {
        const string generated = "tests/Pricing.Data.Tests/baseline.log";
        var model = new ScriptedChatClient(
            Say("Approach: add QuoteStatus.cs"),
            Call("declare_done", new { summary = "verify block already fails" }), Say("done"),
            Call("write_file", new { path = NewFile, content = "public enum QuoteStatus { Draft }\n" }),
            Call("declare_done", new { summary = "added QuoteStatus" }), Say("done"));
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3), suiteCommand: $"echo log > {generated}");

        var result = await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(T001()), NoAnswers, default);

        Assert.Equal(TeamStatus.Passed, result.Status);
        var everCommitted = await h.GitIn(h.Services.Git.IntegrationPath(TeamHarness.RunId), "log", "--all", "--name-only", "--format=");
        Assert.DoesNotContain(generated, everCommitted.Output);
    }

    /// live1: the red run's P check failed on NU1301 (no network), was taken as red, and the
    /// Implementer then had no way to make it pass. It is an infrastructure fault: park.
    [Fact]
    public async Task A_red_run_the_sandbox_could_not_run_parks_instead_of_reaching_the_Implementer()
    {
        var task = Plans.Task("T-001",
            Plans.Auto("file exists", $"test -f {NewFile}"),
            Plans.Auto("P model check", "echo 'P.csproj : error NU1301: Unable to load the service index for source https://api.nuget.org/v3/index.json.'; exit 1"));
        var model = new ScriptedChatClient(
            Say("Approach: add QuoteStatus.cs"),
            Call("declare_done", new { summary = "verify block already fails" }), Say("done"));
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3));

        var result = await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(task), NoAnswers, default);

        Assert.Equal(TeamStatus.Blocked, result.Status);
        Assert.Equal(0, model.Remaining); // no Implementer turn
        Assert.Equal("environment", result.Blocked!.Source);
        Assert.Contains("P model check", result.Blocked.Question);
        Assert.DoesNotContain(h.Rows(LedgerKinds.TaskEvent), e => e.Payload?.Contains("tests_authored") == true);
    }

    [Fact]
    public async Task Failing_verify_gets_one_verify_row_per_attempt_then_fails_the_team()
    {
        var never = Plans.Task("T-001", Plans.Auto("never passes", "test -f does-not-exist"));
        var model = new ScriptedChatClient(
            Say("Approach: try"),
            Call("declare_done", new { summary = "tests" }), Say("done"),
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
            Call("write_file", new { path = TestFile, content = "test -f src/Pricing.Data/QuoteStatus.cs\n" }), Say("still working"));
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
            Call("declare_done", new { summary = "tests" }), Say("done"),
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

    private static IEnumerable<string> ToolResults(ScriptedChatClient model) =>
        model.Requests.SelectMany(r => r).SelectMany(m => m.Contents)
            .OfType<Microsoft.Extensions.AI.FunctionResultContent>()
            .DistinctBy(f => f.CallId) // each request re-sends the turn so far
            .Select(f => f.Result?.ToString() ?? "");

    private static PlannedTask WithTest() => Plans.Task("T-001", Plans.Auto("status test", $"sh {TestFile}"));

    [Fact]
    public async Task TestAuthor_writes_only_tests_and_Implementer_writes_only_code()
    {
        const string realTest = "test -f src/Pricing.Data/QuoteStatus.cs\n";
        var model = new ScriptedChatClient(
            Say("Approach: test first"),
            // test author: code is refused, the test is not
            Call("write_file", new { path = NewFile, content = "// sneaky\n" }),
            Call("write_file", new { path = TestFile, content = realTest }),
            Call("declare_done", new { summary = "status test" }), Say("done"),
            // implementer: the test is refused, the code is not
            Call("write_file", new { path = TestFile, content = "exit 0\n" }),
            Call("write_file", new { path = NewFile, content = "public enum QuoteStatus { Draft }\n" }),
            Call("declare_done", new { summary = "added" }), Say("done"));
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3));

        var result = await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(WithTest()), NoAnswers, default);

        Assert.Equal(TeamStatus.Passed, result.Status);
        var denied = ToolResults(model).Where(r => r.StartsWith("DENIED")).ToList();
        Assert.Equal(2, denied.Count);
        Assert.Contains(NewFile, denied[0]);
        Assert.Contains(TestFile, denied[1]);
        Assert.Equal([Roles.TestAuthor, Roles.Implementer],
            h.Rows(LedgerKinds.ToolCall).Where(e => e.Outcome == "denied").Select(e => e.Stage));

        Assert.Single(h.Rows(LedgerKinds.Verify), e => e.Stage == "red_run" && e.Outcome == "failed");
        var integration = h.Services.Git.IntegrationPath(TeamHarness.RunId);
        Assert.Equal(realTest, await File.ReadAllTextAsync(Path.Combine(integration, TestFile)));
        Assert.Equal("public enum QuoteStatus { Draft }\n", await File.ReadAllTextAsync(Path.Combine(integration, NewFile)));
    }

    [Fact]
    public async Task A_test_that_passes_against_the_stub_never_leaves_TestAuthor()
    {
        var model = new ScriptedChatClient(
            Say("Approach: test first"),
            Call("write_file", new { path = TestFile, content = "exit 0\n" }),
            Call("declare_done", new { summary = "tests" }), Say("done"),
            Call("write_file", new { path = TestFile, content = "true\n" }),
            Call("declare_done", new { summary = "tests again" }), Say("done"));
        using var h = await TeamHarness.CreateAsync(model, new Limits(3, 3));

        var result = await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(WithTest()), NoAnswers, default);

        Assert.Equal(TeamStatus.Failed, result.Status);
        Assert.Contains("TurnBudgetExhausted", result.Failure);
        Assert.Equal(0, model.Remaining);
        Assert.Equal(2, h.Rows(LedgerKinds.Verify).Count(e => e.Stage == "red_run"));
        Assert.DoesNotContain(h.Rows(LedgerKinds.TaskEvent), e => e.Payload!.Contains("tests_authored"));
        Assert.Contains(model.Requests.SelectMany(r => r), m => m.Text.Contains("pass without the implementation"));
    }

    [Fact]
    public async Task A_vacuous_test_fails_the_stash_check_and_goes_back_to_TestAuthor()
    {
        // The test checks an ignored build artefact, which stashing the implementation leaves behind.
        const string artefact = "src/Pricing.Data/bin/Pricing.Data.dll";
        var model = new ScriptedChatClient(
            Say("Approach: test first"),
            Call("write_file", new { path = TestFile, content = $"test -f {artefact}\n" }),
            Call("declare_done", new { summary = "tests" }), Say("done"),
            Call("write_file", new { path = artefact, content = "binary\n" }),
            Call("declare_done", new { summary = "built" }), Say("done"),
            Say("I cannot make this test meaningful"));
        using var h = await TeamHarness.CreateAsync(model, new Limits(4, 3));

        var result = await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(WithTest()), NoAnswers, default);

        Assert.NotEqual(TeamStatus.Passed, result.Status);
        Assert.Equal(0, model.Remaining);
        Assert.Single(h.Rows(LedgerKinds.Verify), e => e.Stage == "stash_check");
        var events = h.Rows(LedgerKinds.TaskEvent).ToList();
        var verified = events.FindIndex(e => e.Payload!.Contains("verify_completed"));
        Assert.Equal("Ready", events[verified].Outcome);
        Assert.DoesNotContain(events, e => e.Outcome == "Passed");
        Assert.Contains(model.Requests.Last(), m => m.Text.Contains("do not exercise the change"));
    }

    [Fact]
    public async Task Breaking_a_test_that_passed_before_blocks_Passed_until_it_passes_again()
    {
        const string quote = "src/Pricing.Data/Quote.cs";
        var model = new ScriptedChatClient(
            Say("Approach: replace Quote.cs with QuoteStatus.cs"),
            Call("declare_done", new { summary = "verify block already fails" }), Say("done"),
            // implementer breaks Quote.exists on the way
            Call("delete_file", new { path = quote }),
            Call("write_file", new { path = NewFile, content = "public enum QuoteStatus { Draft }\n" }),
            Call("declare_done", new { summary = "moved" }), Say("done"),
            // ... and puts it back after seeing the regression
            Call("write_file", new { path = quote, content = "// quote\n" }),
            Call("declare_done", new { summary = "restored Quote.cs" }), Say("done"));
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3), TeamHarness.SuiteCommand);

        var result = await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(T001()), NoAnswers, default);

        Assert.Equal(TeamStatus.Passed, result.Status);
        Assert.Equal(0, model.Remaining);
        Assert.Single(h.Rows(LedgerKinds.Verify), e => e.Stage == "baseline");
        Assert.Equal(["failed", "passed"], h.Rows(LedgerKinds.Verify).Where(e => e.Stage == "regression").Select(e => e.Outcome));
        Assert.Equal(2, h.Rows(LedgerKinds.Verify).Count(e => e.Stage == "verify"));
        Assert.Contains(model.Requests.SelectMany(r => r), m => m.Text.Contains("What failed last time") && m.Text.Contains("Quote.exists"));
        Assert.Single(h.Rows(LedgerKinds.TaskEvent), e => e.Outcome == "Passed");
    }
}
