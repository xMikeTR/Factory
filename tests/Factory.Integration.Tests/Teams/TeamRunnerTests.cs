using Factory.Core;
using Factory.Harness;
using Factory.Harness.Policy;
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

    /// live2: the P check's `p compile` left generated C# inside the test project, and every later
    /// dotnet check in the same run compiled it and failed. Each check starts from the tree as found.
    [Fact]
    public async Task A_check_never_sees_the_files_an_earlier_check_generated()
    {
        const string generated = "tests/Pricing.Data.Tests/P/PGenerated/Gen.cs";
        var task = Plans.Task("T-001",
            Plans.Auto("generates", $"mkdir -p tests/Pricing.Data.Tests/P/PGenerated && echo gen > {generated}"),
            Plans.Auto("needs a clean tree", $"test ! -e {generated} && test -f {NewFile}"));
        var model = new ScriptedChatClient(
            Say("Approach: add QuoteStatus.cs"),
            Call("declare_done", new { summary = "verify block already fails" }), Say("done"),
            Call("write_file", new { path = NewFile, content = "public enum QuoteStatus { Draft }\n" }),
            Call("declare_done", new { summary = "added QuoteStatus" }), Say("done"));
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3));

        var result = await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(task), NoAnswers, default);

        Assert.Equal(TeamStatus.Passed, result.Status);
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

    /// A check that times out (twice: the gate re-runs it alone once) says nothing about the code:
    /// not red, not green. The task parks as an environment problem.
    [Fact]
    public async Task A_red_run_that_times_out_is_not_red_evidence_and_parks()
    {
        var task = Plans.Task("T-001", Plans.Auto("slow check", "sleep 5", timeoutSeconds: 1));
        var model = new ScriptedChatClient(
            Say("Approach: add QuoteStatus.cs"),
            Call("declare_done", new { summary = "tests" }), Say("done"));
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3));

        var result = await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(task), NoAnswers, default);

        Assert.Equal(TeamStatus.Blocked, result.Status);
        Assert.Equal("environment", result.Blocked!.Source);
        Assert.Contains("slow check", result.Blocked.Question);
        Assert.Equal(0, model.Remaining);
        Assert.DoesNotContain(h.Rows(LedgerKinds.TaskEvent), e => e.Payload?.Contains("tests_authored") == true);
    }

    [Fact]
    public async Task A_green_verify_that_times_out_parks_instead_of_counting_an_attempt_as_failed_code()
    {
        var task = Plans.Task("T-001", Plans.Auto("slow once built", $"test -f {NewFile} && sleep 5", timeoutSeconds: 1));
        var model = new ScriptedChatClient(
            Say("Approach: add QuoteStatus.cs"),
            Call("declare_done", new { summary = "tests" }), Say("done"),
            Call("write_file", new { path = NewFile, content = "public enum QuoteStatus { Draft }\n" }),
            Call("declare_done", new { summary = "added" }), Say("done"));
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3));

        var result = await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(task), NoAnswers, default);

        Assert.Equal(TeamStatus.Blocked, result.Status);
        Assert.Equal("environment", result.Blocked!.Source);
        Assert.Contains("slow once built", result.Blocked.Question);
        Assert.Equal(0, model.Remaining); // no second Implementer turn to "fix" a timeout
    }

    private const string Branchy = "class QuoteStatus { int F(int x) { if (x > 1) x++; if (x > 2) x++; if (x > 3) x++; return x; } }\n";
    private static readonly QualityThresholds Tight = new(2, 30.0, 80);

    [Fact]
    public async Task In_block_mode_new_code_over_a_quality_threshold_fails_the_verify_and_the_Implementer_fixes_it()
    {
        var model = new ScriptedChatClient(
            Say("Approach: add QuoteStatus.cs"),
            Call("declare_done", new { summary = "tests" }), Say("done"),
            Call("write_file", new { path = NewFile, content = Branchy }), Call("declare_done", new { summary = "too branchy" }), Say("done"),
            Call("write_file", new { path = NewFile, content = "public enum QuoteStatus { Draft }\n" }), Call("declare_done", new { summary = "simpler" }), Say("done"));
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3), qualityPolicy: new QualityPolicy(QualityMode.Block, Tight));

        var result = await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(T001()), NoAnswers, default);

        Assert.Equal(TeamStatus.Passed, result.Status);
        Assert.Equal(0, model.Remaining);
        Assert.Contains(h.Rows(LedgerKinds.Verify), e => e.Stage == "quality" && e.Outcome == "blocked" && e.Payload!.Contains("QuoteStatus.F"));
    }

    [Fact]
    public async Task In_block_mode_a_project_reference_the_model_does_not_allow_fails_the_verify()
    {
        const string csproj = "src/Pricing.Data/Pricing.Data.csproj";
        const string refsRating = "<Project><ItemGroup><ProjectReference Include=\"..\\Rating.Engine\\Rating.Engine.csproj\" /></ItemGroup></Project>\n";
        var model = new ScriptedChatClient(
            Say("Approach: add QuoteStatus.cs"),
            Call("declare_done", new { summary = "tests" }), Say("done"),
            Call("write_file", new { path = NewFile, content = "public enum QuoteStatus { Draft }\n" }),
            Call("write_file", new { path = csproj, content = refsRating }), Call("declare_done", new { summary = "referenced rating" }), Say("done"),
            Call("write_file", new { path = csproj, content = "<Project />\n" }), Call("declare_done", new { summary = "dropped the reference" }), Say("done"));
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3), qualityPolicy: new QualityPolicy(QualityMode.Block, new QualityThresholds(50, 100.0, 500)));

        var result = await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(T001()), NoAnswers, default);

        Assert.Equal(TeamStatus.Passed, result.Status);
        Assert.Contains(h.Rows(LedgerKinds.Verify), e => e.Stage == "quality" && e.Outcome == "blocked" && e.Payload!.Contains("Quotes DB") && e.Payload.Contains("Rating Engine"));
    }

    [Fact]
    public async Task In_report_mode_the_same_code_passes_and_the_finding_is_in_the_ledger()
    {
        var model = new ScriptedChatClient(
            Say("Approach: add QuoteStatus.cs"),
            Call("declare_done", new { summary = "tests" }), Say("done"),
            Call("write_file", new { path = NewFile, content = Branchy }), Call("declare_done", new { summary = "branchy" }), Say("done"));
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3), qualityPolicy: new QualityPolicy(QualityMode.Report, Tight));

        var result = await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(T001()), NoAnswers, default);

        Assert.Equal(TeamStatus.Passed, result.Status);
        Assert.Contains(h.Rows(LedgerKinds.Verify), e => e.Stage == "quality" && e.Outcome == "reported");
    }

    private static Mutant Survivor(string id = "m1") => new(id, NewFile, 1, "Equality", MutantStatus.Survived);
    private static Mutant Killed(string id = "m2") => new(id, NewFile, 1, "Arithmetic", MutantStatus.Killed);
    private static QualityPolicy MutationOn(QualityMode mode) =>
        new(QualityMode.Off, Tight, new MutationPolicy(mode, new MutationThresholds(0.8)));

    private static ScriptedChatClient TestsThenImpl(params Microsoft.Extensions.AI.ChatResponse[] more) => new([
        Say("Approach: test first"),
        Call("write_file", new { path = TestFile, content = $"test -f {NewFile}\n" }),
        Call("declare_done", new { summary = "tests" }), Say("done"),
        Call("write_file", new { path = NewFile, content = "public enum QuoteStatus { Draft }\n" }),
        Call("declare_done", new { summary = "impl" }), Say("done"),
        .. more]);

    [Fact]
    public async Task In_block_mode_surviving_mutants_send_the_task_back_to_the_TestAuthor_with_the_survivors()
    {
        var model = TestsThenImpl(Say("I cannot strengthen these"));
        using var h = await TeamHarness.CreateAsync(model, new Limits(4, 3), qualityPolicy: MutationOn(QualityMode.Block));
        h.Mutation.Then(Survivor(), Killed());

        var result = await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(WithTest()), NoAnswers, default);

        Assert.NotEqual(TeamStatus.Passed, result.Status);
        Assert.Equal([NewFile], h.Mutation.Asked.Single());
        Assert.Contains(h.Rows(LedgerKinds.Verify), e => e.Stage == "mutation" && e.Outcome == "blocked" && e.Payload!.Contains("QuoteStatus.cs:1"));
        Assert.Contains(h.Rows(LedgerKinds.TaskEvent), e => e.Payload!.Contains("returned_to_test_author"));
        Assert.Contains(model.Requests.Last(), m => m.Text.Contains("mutants of the code this task changed survived"));
    }

    private static Microsoft.Extensions.AI.ChatResponse[] ClaimEquivalent(string id) =>
    [
        Call("declare_equivalent", new { mutantId = id, reason = "the comparison is on an unreachable branch" }),
        Call("declare_done", new { summary = "claimed" }), Say("done"),
        Call("declare_done", new { summary = "impl unchanged" }), Say("done"),
    ];

    [Fact]
    public async Task A_survivor_the_TestAuthor_declared_equivalent_and_the_reviewer_confirmed_is_left_out_of_the_score()
    {
        var model = TestsThenImpl(ClaimEquivalent("m1"));
        using var h = await TeamHarness.CreateAsync(model, new Limits(6, 3), qualityPolicy: MutationOn(QualityMode.Block));
        h.Mutation.Then(Survivor(), Killed()).Then(Survivor(), Killed());
        h.Reviewer.ThenConfirmEquivalent("m1");

        var result = await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(WithTest()), NoAnswers, default);

        Assert.Equal(TeamStatus.Passed, result.Status);
        Assert.Equal("m1", h.Reviewer.EquivalenceRequests.Single().Claims.Single().Mutant.Id);
        Assert.Contains(h.Rows(LedgerKinds.Verify), e => e.Stage == "mutation" && e.Outcome == "equivalent_confirmed");
    }

    [Fact]
    public async Task A_declared_equivalent_the_reviewer_does_not_confirm_still_counts_as_a_survivor_and_goes_back_with_that_said()
    {
        var model = TestsThenImpl(ClaimEquivalent("m1"));
        using var h = await TeamHarness.CreateAsync(model, new Limits(6, 3), qualityPolicy: MutationOn(QualityMode.Block));
        h.Mutation.Then(Survivor(), Killed()).Then(Survivor(), Killed());

        var result = await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(WithTest()), NoAnswers, default);

        Assert.NotEqual(TeamStatus.Passed, result.Status);
        Assert.Contains(h.Rows(LedgerKinds.Verify), e => e.Stage == "mutation" && e.Outcome == "equivalent_rejected");
        Assert.Contains(h.Rows(LedgerKinds.Verify), e => e.Stage == "mutation" && e.Outcome == "blocked" && e.Payload!.Contains("reviewer did not accept it"));
    }

    [Fact]
    public async Task Only_the_TestAuthor_may_declare_a_mutant_equivalent()
    {
        var policy = new Factory.Harness.RoleAllowListPolicy();
        var none = new Dictionary<string, object?>();
        Assert.True(policy.Evaluate(Factory.Harness.Roles.TestAuthor, "declare_equivalent", none).Allowed);
        Assert.False(policy.Evaluate(Factory.Harness.Roles.Implementer, "declare_equivalent", none).Allowed);
        Assert.False(policy.Evaluate(Factory.Harness.Roles.Reviewer, "declare_equivalent", none).Allowed);
        Assert.True(policy.Evaluate(Factory.Harness.Roles.Reviewer, "confirm_equivalents", none).Allowed);
        Assert.False(policy.Evaluate(Factory.Harness.Roles.TestAuthor, "confirm_equivalents", none).Allowed);
    }

    private const string Existing = "src/Pricing.Data/Quote.cs";
    private static Mutant OnExisting(string id, MutantStatus status) => new(id, Existing, 2, "Equality", status);

    /// The implementation edits a file the baseline already has, so the baseline can be measured.
    private static ScriptedChatClient EditsExisting(params Microsoft.Extensions.AI.ChatResponse[] more) => new([
        Say("Approach: test first"),
        Call("write_file", new { path = TestFile, content = $"grep -q more {Existing}\n" }),
        Call("declare_done", new { summary = "tests" }), Say("done"),
        Call("write_file", new { path = Existing, content = "// quote\n// more\n" }),
        Call("declare_done", new { summary = "impl" }), Say("done"),
        .. more]);

    [Fact]
    public async Task A_file_that_already_scored_lower_on_the_baseline_is_held_to_that_score_not_the_threshold()
    {
        using var h = await TeamHarness.CreateAsync(EditsExisting(), new Limits(4, 3), qualityPolicy: MutationOn(QualityMode.Block));
        h.Mutation.Then(OnExisting("s", MutantStatus.Survived), OnExisting("k", MutantStatus.Killed)) // the task: 50% < 80%
            .Then(OnExisting("b1", MutantStatus.Survived), OnExisting("b2", MutantStatus.Survived), OnExisting("b3", MutantStatus.Killed)); // baseline 33%: 50% is no worse

        var result = await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(WithTest()), NoAnswers, default);

        Assert.Equal(TeamStatus.Passed, result.Status);
        Assert.Equal(2, h.Mutation.Asked.Count);
        Assert.NotEqual(h.Mutation.Worktrees[0], h.Mutation.Worktrees[1]);
        Assert.Equal([Existing], h.Mutation.Asked[1]);
    }

    [Fact]
    public async Task A_file_that_scored_higher_on_the_baseline_still_needs_only_the_threshold()
    {
        using var h = await TeamHarness.CreateAsync(EditsExisting(Say("I cannot strengthen these")), new Limits(4, 3), qualityPolicy: MutationOn(QualityMode.Block));
        h.Mutation.Then(OnExisting("s", MutantStatus.Survived), OnExisting("k", MutantStatus.Killed))
            .Then(OnExisting("b1", MutantStatus.Killed), OnExisting("b2", MutantStatus.Killed)); // baseline 100%; required is min(80%, 100%)

        var result = await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(WithTest()), NoAnswers, default);

        Assert.NotEqual(TeamStatus.Passed, result.Status);
    }

    [Fact]
    public async Task A_mutation_score_on_the_gate_passes()
    {
        var model = TestsThenImpl();
        using var h = await TeamHarness.CreateAsync(model, new Limits(4, 3), qualityPolicy: MutationOn(QualityMode.Block));
        h.Mutation.Then(Killed("a"), Killed("b"), Killed("c"), Killed("d"), Survivor()); // 80%

        var result = await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(WithTest()), NoAnswers, default);

        Assert.Equal(TeamStatus.Passed, result.Status);
    }

    [Fact]
    public async Task In_report_mode_survivors_are_in_the_ledger_and_the_task_passes()
    {
        var model = TestsThenImpl();
        using var h = await TeamHarness.CreateAsync(model, new Limits(4, 3), qualityPolicy: MutationOn(QualityMode.Report));
        h.Mutation.Then(Survivor());

        var result = await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(WithTest()), NoAnswers, default);

        Assert.Equal(TeamStatus.Passed, result.Status);
        Assert.Contains(h.Rows(LedgerKinds.Verify), e => e.Stage == "mutation" && e.Outcome == "reported");
    }

    [Fact]
    public async Task A_mutation_run_that_times_out_parks_the_task_as_an_environment_problem()
    {
        var model = TestsThenImpl();
        using var h = await TeamHarness.CreateAsync(model, new Limits(4, 3), qualityPolicy: MutationOn(QualityMode.Block));
        h.Mutation.ThenTimeout();

        var result = await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(WithTest()), NoAnswers, default);

        Assert.Equal(TeamStatus.Blocked, result.Status);
        Assert.Equal("environment", result.Blocked!.Source);
    }

    [Fact]
    public async Task Tests_that_pass_on_the_existing_code_are_red_evidence_when_they_kill_a_mutant()
    {
        const string existing = "src/Pricing.Data/Quote.cs";
        var model = new ScriptedChatClient(
            Say("Approach: pin the existing behaviour"),
            Call("write_file", new { path = TestFile, content = $"test -f {existing}\n" }),
            Call("declare_done", new { summary = "pins Quote" }), Say("done"),
            Call("write_file", new { path = NewFile, content = "public enum QuoteStatus { Draft }\n" }),
            Call("declare_done", new { summary = "impl" }), Say("done"));
        using var h = await TeamHarness.CreateAsync(model, new Limits(4, 3), qualityPolicy: MutationOn(QualityMode.Block));
        h.Mutation.Then(new Mutant("k1", existing, 1, "Block", MutantStatus.Killed)); // red run: the test killed a mutant of Quote.cs

        var result = await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(WithTest()), NoAnswers, default);

        Assert.Equal(TeamStatus.Passed, result.Status);
        Assert.Contains(h.Rows(LedgerKinds.TaskEvent), e => e.Payload!.Contains("against_mutant"));
    }

    [Fact]
    public async Task Tests_that_pass_on_the_existing_code_and_kill_nothing_stay_with_the_TestAuthor()
    {
        var model = new ScriptedChatClient(
            Say("Approach: pin"),
            Call("write_file", new { path = TestFile, content = "test -f src/Pricing.Data/Quote.cs\n" }),
            Call("declare_done", new { summary = "pins Quote" }), Say("done"));
        using var h = await TeamHarness.CreateAsync(model, new Limits(2, 3), qualityPolicy: MutationOn(QualityMode.Block));
        h.Mutation.Then(new Mutant("s1", "src/Pricing.Data/Quote.cs", 1, "Block", MutantStatus.Survived));

        var result = await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(WithTest()), NoAnswers, default);

        Assert.NotEqual(TeamStatus.Passed, result.Status);
        Assert.DoesNotContain(h.Rows(LedgerKinds.TaskEvent), e => e.Payload!.Contains("against_mutant"));
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
            Call("raise_needs_decision", new { question = "Which quote statuses exist?", options = new[] { new { id = "A", summary = "Draft only", consequence = "one initial status" }, new { id = "B", summary = "Draft and Bound", consequence = "two initial statuses" } }, recommended = "A" }), Say("asked"),
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

    /// live2: the Implementer found the fault was in the test project, outside its writes, and
    /// could only park. Broken tests go back to the TestAuthor instead; no human is needed.
    [Fact]
    public async Task The_Implementer_hands_broken_tests_back_to_the_TestAuthor_instead_of_parking()
    {
        const string fixture = "tests/Pricing.Data.Tests/fixture.txt";
        const string problem = "status.sh needs tests/Pricing.Data.Tests/fixture.txt, which is outside my writes";
        var model = new ScriptedChatClient(
            Say("Approach: test first"),
            Call("write_file", new { path = TestFile, content = $"test -f {NewFile} && test -f {fixture}\n" }),
            Call("declare_done", new { summary = "status test" }), Say("done"),
            // implementer: writes the code, finds the test can never pass
            Call("write_file", new { path = NewFile, content = "public enum QuoteStatus { Draft }\n" }),
            Call("return_to_test_author", new { problem }), Say("handed back"),
            // test author again
            Call("write_file", new { path = fixture, content = "x\n" }),
            Call("declare_done", new { summary = "added the fixture" }), Say("done"),
            // implementer again: its code is still there
            Call("declare_done", new { summary = "code unchanged" }), Say("done"));
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3));

        var result = await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(WithTest()), NoAnswers, default);

        Assert.Equal(TeamStatus.Passed, result.Status);
        Assert.Equal(0, model.Remaining);
        Assert.Empty(h.Rows(LedgerKinds.NeedsDecision));
        Assert.Contains(h.Rows(LedgerKinds.TaskEvent), e => e.Payload!.Contains("returned_to_test_author"));
        Assert.Equal(2, h.Rows(LedgerKinds.Verify).Count(e => e.Stage == "red_run"));
        Assert.Contains(model.Requests.SelectMany(r => r), m => m.Text.Contains(problem));
        var integration = h.Services.Git.IntegrationPath(TeamHarness.RunId);
        Assert.True(File.Exists(Path.Combine(integration, fixture)));
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
