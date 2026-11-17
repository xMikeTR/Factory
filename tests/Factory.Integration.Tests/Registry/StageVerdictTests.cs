using System.Text.Json;
using Factory.Harness;
using Factory.Harness.Registry;
using Factory.Integration.Tests.Support;
using Xunit;

namespace Factory.Integration.Tests.Registry;

/// The projection's `stage_verdict` rows (designc D7): which stage said what about which task and screen.
public class StageVerdictTests
{
    private const string RunId = "run-1";
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeMilliseconds(1_791_400_000_000);
    private static readonly HandoverRef Handover = new("20261007-c4d072ba", [], "W-ui");

    private static LedgerEvent At(int second, LedgerEvent e) => e with { Ts = T0.AddSeconds(second), TraceId = null };

    private static LedgerEvent UiFail(int s, string task, string kind, string? state = null, string? placement = null, string screen = "bordereaux", string? detail = null) =>
        At(s, new(RunId, "S2", "integration_verify", LedgerKinds.UiCheckFailed, TaskId: task, Element: "App/Portal", Outcome: "failed",
            Payload: JsonSerializer.Serialize(new { screen, state, placement, kind, detail, commit = "abc", feedback = state is null ? null : "SECRET diff text" })));

    private static LedgerEvent Holdout(int s, string task, string screen, string state, string? criterion = null) =>
        At(s, new(RunId, "S2", "integration_verify", LedgerKinds.HoldoutFailed, TaskId: task, Element: "App/Portal", Outcome: "failed",
            Payload: JsonSerializer.Serialize(criterion is null ? new { screen, state, criterion = (string?)null } : new { screen, state, criterion })));

    internal static List<JsonElement> Verdicts(IEnumerable<LedgerEvent> run) =>
        RegistryLedgerProjector.Project(run.ToList(), Handover, new(5, 3))
            .Where(e => e.Line.Contains("\"kind\":\"stage_verdict\"")).Select(e => JsonDocument.Parse(e.Line).RootElement.Clone()).ToList();

    private static string S(JsonElement e, string p) => e.GetProperty(p).GetString()!;
    internal static List<JsonElement> Of(List<JsonElement> all, string stage) => all.Where(v => S(v, "stage") == stage).ToList();

    [Fact]
    public void A_code_failure_is_a_ui_code_fail_naming_task_element_and_screen_and_no_detail()
    {
        var v = Assert.Single(Verdicts([UiFail(1, "T-1", "code", detail: "SECRET rule: raw colour")]));
        Assert.Equal(("ui-code", "fail", "T-1", "App/Portal", "bordereaux", "W-ui", RunId),
            (S(v, "stage"), S(v, "outcome"), S(v, "task"), S(v, "element"), S(v, "screen"), S(v, "item"), S(v, "run")));
        Assert.Equal("code", S(v.GetProperty("failures")[0], "kind"));
        Assert.DoesNotContain("SECRET", v.GetRawText());
    }

    [Fact]
    public void Aria_pixels_and_placement_failures_are_one_ui_visual_fail_per_task_and_screen_with_merged_failures()
    {
        var v = Assert.Single(Verdicts([
            UiFail(1, "T-1", "aria", "ready"),
            UiFail(2, "T-1", "pixels", "empty"),
            UiFail(3, "T-1", "placement-pixels", "ready", "header"),
            UiFail(4, "T-1", "placement-missing", "ready", "footer"),
            UiFail(5, "T-1", "aria", "ready"), // a repeat adds nothing
        ]));
        Assert.Equal("ui-visual", S(v, "stage"));
        var failures = v.GetProperty("failures").EnumerateArray().Select(f => (f.GetProperty("state").GetString(), S(f, "kind"), f.TryGetProperty("placement", out var p) ? p.GetString() : null)).ToList();
        Assert.Equal([("ready", "aria", null), ("empty", "pixels", null), ("ready", "placement-pixels", "header"), ("ready", "placement-missing", "footer")], failures);
    }

    [Fact]
    public void A_request_failure_is_a_ui_code_fail_because_the_endpoint_is_a_code_fault_not_a_look()
    {
        var v = Assert.Single(Verdicts([UiFail(1, "T-1", "request", detail: "unlisted request: GET /api/unlisted")]));
        Assert.Equal(("ui-code", "fail"), (S(v, "stage"), S(v, "outcome")));
        Assert.DoesNotContain("/api/unlisted", v.GetRawText());
    }

    [Fact]
    public void Code_and_visual_failures_of_one_task_are_two_verdicts_and_two_tasks_are_two_rows()
    {
        var all = Verdicts([UiFail(1, "T-1", "code"), UiFail(2, "T-1", "aria", "ready"), UiFail(3, "T-2", "aria", "ready")]);
        Assert.Equal(["ui-code", "ui-visual", "ui-visual"], all.Select(v => S(v, "stage")));
        Assert.Equal(["T-1", "T-1", "T-2"], all.Select(v => S(v, "task")));
    }

    private static LedgerEvent UiVerify(int s, string screen, string outcome, int exit) =>
        At(s, new(RunId, "S2", "integration_verify", LedgerKinds.Verify, Outcome: outcome,
            Payload: JsonSerializer.Serialize(new { name = "ui:" + screen, ExitCode = exit, kinds = Array.Empty<string>() })));

    [Fact]
    public void A_passing_ui_verify_with_no_failure_for_the_screen_is_one_pass_for_ui_code_and_one_for_ui_visual_with_no_task()
    {
        var all = Verdicts([UiVerify(1, "bordereaux", "passed", 0)]);
        Assert.Equal([("ui-code", "pass"), ("ui-visual", "pass")], all.Select(v => (S(v, "stage"), S(v, "outcome"))));
        Assert.All(all, v => { Assert.False(v.TryGetProperty("task", out _)); Assert.Equal("bordereaux", S(v, "screen")); });
    }

    [Fact]
    public void A_pass_is_not_written_for_a_screen_that_had_a_ui_check_failure_in_the_run_and_a_not_run_screen_is_could_not_run()
    {
        var failed = Verdicts([UiFail(1, "T-1", "aria", "ready"), UiVerify(2, "bordereaux", "passed", 0)]);
        Assert.Equal(["fail"], failed.Select(v => S(v, "outcome")));

        var notRun = Verdicts([UiVerify(1, "bordereaux", "failed", 3)]);
        Assert.Equal(["could_not_run", "could_not_run"], notRun.Select(v => S(v, "outcome")));
    }

    [Fact]
    public void A_ui_holdout_failure_is_a_holdout_fail_with_state_only_and_an_acceptance_one_adds_the_criterion()
    {
        var all = Verdicts([Holdout(1, "T-1", "bordereaux", "empty"), Holdout(2, "T-1", "bordereaux", "error"), Holdout(3, "T-1", "bordereaux", "ready", "AC-7")]);
        var ui = all.First(v => !v.GetProperty("failures")[0].TryGetProperty("criterion", out _));
        Assert.Equal("holdout", S(ui, "stage"));
        Assert.Equal(["empty", "error"], ui.GetProperty("failures").EnumerateArray().Select(f => S(f, "state")));
        Assert.All(ui.GetProperty("failures").EnumerateArray(), f => Assert.Single(f.EnumerateObject()));
        var acceptance = all.Single(v => v.GetProperty("failures")[0].TryGetProperty("criterion", out _));
        Assert.Equal(("AC-7", "ready"), (S(acceptance.GetProperty("failures")[0], "criterion"), S(acceptance.GetProperty("failures")[0], "state")));
    }

    private static LedgerEvent HoldoutVerify(int s, string? screen, string outcome, string? kind = null) =>
        At(s, new(RunId, "S2", "integration_verify", LedgerKinds.Verify, Outcome: outcome == "pass" ? "passed" : "failed",
            Payload: JsonSerializer.Serialize(new { name = "holdout", screen, kind, outcome, ms = 5, states_failed = Array.Empty<string>() })));

    [Fact]
    public void The_holdout_verify_row_gives_pass_and_could_not_run_and_no_holdout_gives_nothing()
    {
        var all = Verdicts([
            HoldoutVerify(1, "a", "pass"), HoldoutVerify(2, "b", "could_not_run"), HoldoutVerify(3, "c", "no_holdout"),
            HoldoutVerify(4, null, "pass", "acceptance")]);
        Assert.Equal([("a", "pass"), ("b", "could_not_run")],
            all.Where(v => v.TryGetProperty("screen", out _)).Select(v => (S(v, "screen"), S(v, "outcome"))));
        Assert.Equal(3, all.Count);
        Assert.Equal("pass", S(all.Last(), "outcome"));
        Assert.False(all.Last().TryGetProperty("screen", out _));
    }

    [Fact]
    public void A_failed_holdout_verify_with_no_blamed_task_is_a_run_level_fail_with_the_failed_states()
    {
        var all = Verdicts([At(1, new(RunId, "S2", "integration_verify", LedgerKinds.Verify, Outcome: "failed",
            Payload: """{"name":"holdout","screen":"a","outcome":"fail","ms":5,"states_failed":["empty"]}"""))]);
        var v = Assert.Single(all);
        Assert.Equal(("holdout", "fail", "a"), (S(v, "stage"), S(v, "outcome"), S(v, "screen")));
        Assert.False(v.TryGetProperty("task", out _));
        Assert.Equal("empty", S(v.GetProperty("failures")[0], "state"));
    }

    [Fact]
    public void Every_projected_verdict_validates_against_the_pinned_schema()
    {
        var run = new[] { UiFail(1, "T-1", "code", detail: "x"), UiFail(2, "T-1", "aria", "ready"), Holdout(3, "T-1", "bordereaux", "empty"), UiVerify(4, "other", "passed", 0) };
        foreach (var e in RegistryLedgerProjector.Project(run.ToList(), Handover, new(5, 3)).Where(e => e.Line.Contains("stage_verdict")))
        {
            Assert.Empty(DesigncSchema.Validate(e.Line));
            Assert.Equal("pipeline.jsonl", e.Stream);
        }
    }
}

public class StageVerdictAcceptanceReviewQaTests
{
    private const string RunId = "run-1";
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeMilliseconds(1_791_400_000_000);
    private static LedgerEvent At(int second, LedgerEvent e) => e with { Ts = T0.AddSeconds(second), TraceId = null };
    private static string S(JsonElement e, string p) => e.GetProperty(p).GetString()!;
    private static List<JsonElement> Verdicts(params LedgerEvent[] run) => StageVerdictTests.Verdicts(run);

    private static LedgerEvent AcFail(int s, string task, string ac, string? element = "App/Portal") =>
        At(s, new(RunId, "S2", "integration_verify", LedgerKinds.UiAcceptanceFailed, TaskId: task, Element: element, Outcome: "failed",
            Payload: $$"""{"ac":"{{ac}}","spec":"tests/acceptance/portal/{{ac}}.spec.ts","commit":"abc","trace":"SECRET trace"}"""));

    private static LedgerEvent AcVerify(int s, string outcome, string stage = "integration_verify") =>
        At(s, new(RunId, "S2", stage, LedgerKinds.Verify, Outcome: outcome,
            Payload: """{"name":"acceptance","command":"npx playwright test","ExitCode":0,"ms":9,"failing":[],"tail":"SECRET tail"}"""));

    [Fact]
    public void A_failing_frozen_spec_is_an_acceptance_fail_per_task_with_the_criterion_and_no_trace()
    {
        var all = Verdicts(AcFail(1, "T-1", "AC-1"), AcFail(2, "T-1", "AC-2"), AcFail(3, "T-2", "AC-1"));
        Assert.Equal([("T-1", 2), ("T-2", 1)], all.Select(v => (S(v, "task"), v.GetProperty("failures").GetArrayLength())));
        Assert.All(all, v => { Assert.Equal(("acceptance", "fail"), (S(v, "stage"), S(v, "outcome"))); Assert.DoesNotContain("SECRET", v.GetRawText()); });
        Assert.Equal("AC-1", S(all[0].GetProperty("failures")[0], "criterion"));
    }

    [Fact]
    public void A_passing_acceptance_verify_is_a_run_level_pass_and_the_holdout_runs_own_acceptance_row_is_not_one()
    {
        var v = Assert.Single(Verdicts(AcVerify(1, "passed"), AcVerify(2, "passed", "holdout_acceptance")));
        Assert.Equal(("acceptance", "pass"), (S(v, "stage"), S(v, "outcome")));
        Assert.False(v.TryGetProperty("task", out _));
        Assert.DoesNotContain("SECRET", v.GetRawText());
    }

    [Fact]
    public void A_failed_acceptance_run_that_blames_no_task_is_still_a_run_level_fail()
    {
        var v = Assert.Single(Verdicts(AcVerify(1, "failed")));
        Assert.Equal(("acceptance", "fail"), (S(v, "stage"), S(v, "outcome")));
        Assert.Empty(Verdicts(AcVerify(1, "failed"), AcFail(2, "T-1", "AC-1")).Where(x => !x.TryGetProperty("task", out _)));
    }

    private static LedgerEvent Review(int s, string task, string outcome, int round) =>
        At(s, new(RunId, "S2", "review", LedgerKinds.Review, TaskId: task, TeamId: "team-1", Outcome: outcome,
            Payload: $$$"""{"round":{{{round}}},"tree":"t","detail":{"blocking":[{"message":"SECRET finding"}]}}"""));

    private static LedgerEvent TaskRow(int s, string task, string element) =>
        At(s, new(RunId, "S2", "task", LedgerKinds.TaskEvent, TaskId: task, TeamId: "team-1", Element: element, Outcome: "Running"));

    [Theory]
    [InlineData("passed", "pass")]
    [InlineData("accepted", "pass")]
    [InlineData("blocked", "fail")]
    [InlineData("malformed", "could_not_run")]
    public void A_review_round_maps_its_outcome_and_carries_its_round(string source, string outcome)
    {
        var v = Assert.Single(Verdicts(TaskRow(1, "T-1", "App/Portal"), Review(2, "T-1", source, 2)));
        Assert.Equal(("review", outcome, "T-1", "App/Portal", 2), (S(v, "stage"), S(v, "outcome"), S(v, "task"), S(v, "element"), v.GetProperty("round").GetInt32()));
        Assert.DoesNotContain("SECRET", v.GetRawText());
    }

    [Fact]
    public void Each_review_round_is_its_own_verdict_so_a_fail_then_a_pass_shows_the_recovery()
    {
        var all = Verdicts(Review(1, "T-1", "blocked", 1), Review(2, "T-1", "passed", 2));
        Assert.Equal([("fail", 1), ("pass", 2)], all.Select(v => (S(v, "outcome"), v.GetProperty("round").GetInt32())));
    }

    private static LedgerEvent Qa(int s, string outcome) =>
        At(s, new(RunId, "S2", "qa", LedgerKinds.Verify, Outcome: outcome, Payload: """{"requirements":[],"tests":2,"findings":["SECRET finding"]}"""));

    [Theory]
    [InlineData("passed", "pass")]
    [InlineData("failed", "fail")]
    [InlineData("timeout", "could_not_run")]
    public void Qa_is_a_run_level_verdict(string source, string outcome)
    {
        var v = Assert.Single(Verdicts(Qa(1, source)));
        Assert.Equal(("qa", outcome), (S(v, "stage"), S(v, "outcome")));
        Assert.False(v.TryGetProperty("task", out _));
        Assert.DoesNotContain("SECRET", v.GetRawText());
    }
}

public class StageVerdictChangeTsTests
{
    private const string RunId = "run-1";
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeMilliseconds(1_791_400_000_000);
    private static long Ms(int second) => T0.AddSeconds(second).ToUnixTimeMilliseconds();
    private static LedgerEvent At(int second, LedgerEvent e) => e with { Ts = T0.AddSeconds(second), TraceId = null };

    private static LedgerEvent Requested(int s, string task) =>
        At(s, new(RunId, "S2", "task", LedgerKinds.TaskEvent, TaskId: task, TeamId: "t", Outcome: "Verifying", Payload: """{"type":"verify_requested"}"""));

    private static LedgerEvent Other(int s, string task) =>
        At(s, new(RunId, "S2", "task", LedgerKinds.TaskEvent, TaskId: task, TeamId: "t", Outcome: "Running", Payload: """{"type":"turn_started"}"""));

    private static LedgerEvent UiFail(int s, string task) =>
        At(s, new(RunId, "S2", "integration_verify", LedgerKinds.UiCheckFailed, TaskId: task, Outcome: "failed", Payload: """{"screen":"a","state":"ready","kind":"aria","commit":"c"}"""));

    private static LedgerEvent Qa(int s) => At(s, new(RunId, "S2", "qa", LedgerKinds.Verify, Outcome: "failed", Payload: "{}"));

    private static long? ChangeTs(JsonElement v) => v.GetProperty("change_ts") is { ValueKind: JsonValueKind.Number } n ? n.GetInt64() : null;

    [Fact]
    public void A_task_verdicts_change_ts_is_the_tasks_latest_verify_requested_before_it()
    {
        var v = Assert.Single(StageVerdictTests.Verdicts([Requested(1, "T-1"), Requested(3, "T-1"), Requested(9, "T-1"), Other(4, "T-1"), Requested(2, "T-2"), UiFail(5, "T-1")]));
        Assert.Equal(Ms(3), ChangeTs(v));
    }

    [Fact]
    public void A_verify_requested_at_the_verdicts_own_time_counts_and_a_later_one_does_not()
    {
        Assert.Equal(Ms(5), ChangeTs(Assert.Single(StageVerdictTests.Verdicts([Requested(5, "T-1"), UiFail(5, "T-1")]))));
        Assert.Null(ChangeTs(Assert.Single(StageVerdictTests.Verdicts([UiFail(5, "T-1"), Requested(6, "T-1")]))));
    }

    [Fact]
    public void A_task_with_no_verify_requested_gives_null()
    {
        Assert.Null(ChangeTs(Assert.Single(StageVerdictTests.Verdicts([Requested(1, "T-2"), UiFail(5, "T-1")]))));
    }

    [Fact]
    public void A_run_level_verdicts_change_ts_is_the_latest_verify_requested_of_any_task_before_it()
    {
        var v = Assert.Single(StageVerdictTests.Verdicts([Requested(1, "T-1"), Requested(4, "T-2"), Requested(2, "T-3"), Qa(6), Requested(8, "T-1")]));
        Assert.Equal(Ms(4), ChangeTs(v));
    }

    [Fact]
    public void A_run_with_no_verify_requested_at_all_gives_null_for_a_run_level_verdict()
    {
        Assert.Null(ChangeTs(Assert.Single(StageVerdictTests.Verdicts([Qa(6)]))));
    }

    [Fact]
    public void The_change_ts_is_never_later_than_the_verdicts_own_ts_so_every_row_validates()
    {
        var run = new[] { Requested(1, "T-1"), UiFail(5, "T-1"), Qa(6) };
        foreach (var e in RegistryLedgerProjector.Project(run.ToList(), new("h", []), new(5, 3)).Where(e => e.Line.Contains("stage_verdict")))
            Assert.Empty(DesigncSchema.Validate(e.Line));
    }
}
