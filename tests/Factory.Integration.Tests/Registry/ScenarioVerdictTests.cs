using System.Text.Json;
using Factory.Harness;
using Factory.Harness.Registry;
using Xunit;

namespace Factory.Integration.Tests.Registry;

/// F1: the projection's `scenario` stage_verdict rows, in designc's B3c shape: failures carry `{criterion, kind}` only (no key, no msg,
/// no detail text), and `scenario` names the scenario only when exactly one failed.
public class ScenarioVerdictTests
{
    private const string RunId = "run-1";
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeMilliseconds(1_791_400_000_000);
    private static LedgerEvent At(int second, LedgerEvent e) => e with { Ts = T0.AddSeconds(second), TraceId = null };
    private static string S(JsonElement e, string p) => e.GetProperty(p).GetString()!;

    private static LedgerEvent Fail(int s, string task, string criterion, string scenarioKind, string screen = "orders") =>
        At(s, new(RunId, "S2", "integration_verify", LedgerKinds.UiCheckFailed, TaskId: task, Element: "App/Orders", Outcome: "failed",
            Payload: JsonSerializer.Serialize(new
            {
                screen, kind = "scenario", criterion, scenarioKind, commit = "abc",
                detail = "SECRET " + criterion + " (" + scenarioKind + ")", feedback = "SECRET diff text",
            })));

    private static LedgerEvent Verify(int s, string name, string outcome, int exit, object[]? failures = null, string[]? kinds = null) =>
        At(s, new(RunId, "S2", "integration_verify", LedgerKinds.Verify, Outcome: outcome,
            Payload: JsonSerializer.Serialize(new { name, ExitCode = exit, kinds = kinds ?? [], failures })));

    private static List<JsonElement> Verdicts(params LedgerEvent[] run) => StageVerdictTests.Verdicts(run);

    [Fact]
    public void One_failing_scenario_is_a_scenario_fail_naming_task_screen_scenario_and_the_example_as_criterion()
    {
        var v = Assert.Single(Verdicts(Fail(1, "T-1", "ORD-1-ts-01 example 1", "body"), Fail(2, "T-1", "ORD-1-ts-01 example 2", "body")));
        Assert.Equal(("scenario", "fail", "T-1", "App/Orders", "orders", "ORD-1-ts-01"),
            (S(v, "stage"), S(v, "outcome"), S(v, "task"), S(v, "element"), S(v, "screen"), S(v, "scenario")));
        Assert.Equal([("ORD-1-ts-01 example 1", "body"), ("ORD-1-ts-01 example 2", "body")],
            v.GetProperty("failures").EnumerateArray().Select(f => (S(f, "criterion"), S(f, "kind"))));
        Assert.All(v.GetProperty("failures").EnumerateArray(), f => Assert.Equal(["criterion", "kind"], f.EnumerateObject().Select(p => p.Name).Order()));
        Assert.DoesNotContain("SECRET", v.GetRawText());
    }

    [Fact]
    public void Two_failing_scenarios_name_no_scenario_and_a_not_sent_request_keeps_its_kind()
    {
        var v = Assert.Single(Verdicts(Fail(1, "T-1", "ORD-1-ts-01 example 1", "not_sent"), Fail(2, "T-1", "ORD-2-ts-01 example 1", "could_not_run")));
        Assert.False(v.TryGetProperty("scenario", out _));
        Assert.Equal(["not_sent", "could_not_run"], v.GetProperty("failures").EnumerateArray().Select(f => S(f, "kind")));
    }

    [Fact]
    public void A_scenario_failure_is_not_a_ui_code_or_ui_visual_failure_and_leaves_those_stages_their_own_pass()
    {
        var all = Verdicts(Fail(1, "T-1", "ORD-1-ts-01 example 1", "body"), Verify(2, "ui:orders", "failed", 1, kinds: ["scenario"]));
        Assert.Equal([("scenario", "fail"), ("ui-code", "pass"), ("ui-visual", "pass")], all.Select(v => (S(v, "stage"), S(v, "outcome"))).OrderBy(x => x.Item1));
    }

    [Fact]
    public void A_page_failure_beside_a_scenario_failure_still_blames_the_page_stages()
    {
        var all = Verdicts(Fail(1, "T-1", "ORD-1-ts-01 example 1", "body"),
            At(2, new(RunId, "S2", "integration_verify", LedgerKinds.UiCheckFailed, TaskId: "T-1", Element: "App/Orders", Outcome: "failed",
                Payload: JsonSerializer.Serialize(new { screen = "orders", state = "ready", kind = "aria", commit = "abc" }))),
            Verify(3, "ui:orders", "failed", 1, kinds: ["scenario", "aria"]));
        Assert.Equal(["scenario:fail", "ui-visual:fail"], all.Select(v => S(v, "stage") + ":" + S(v, "outcome")).Order());
    }

    [Fact]
    public void A_passing_or_not_run_scenario_check_is_one_run_level_verdict()
    {
        var pass = Assert.Single(Of(Verdicts(Verify(1, "scenario:orders", "passed", 0)), "scenario"));
        Assert.Equal(("pass", "orders"), (S(pass, "outcome"), S(pass, "screen")));
        Assert.False(pass.TryGetProperty("task", out _));
        Assert.Equal("could_not_run", S(Assert.Single(Of(Verdicts(Verify(1, "scenario:orders", "failed", 3)), "scenario")), "outcome"));
    }

    [Fact]
    public void A_failed_scenario_check_that_blames_no_task_is_a_run_level_fail_with_its_failures()
    {
        var v = Assert.Single(Of(Verdicts(Verify(1, "scenario:orders", "failed", 1,
            failures: [new { criterion = "ORD-1-ts-01 example 1", kind = "body" }])), "scenario"));
        Assert.Equal(("fail", "ORD-1-ts-01"), (S(v, "outcome"), S(v, "scenario")));
        Assert.False(v.TryGetProperty("task", out _));
        Assert.Equal("body", S(v.GetProperty("failures")[0], "kind"));
    }

    private static List<JsonElement> Of(List<JsonElement> all, string stage) => StageVerdictTests.Of(all, stage);
}
