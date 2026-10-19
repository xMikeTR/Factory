using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Microsoft.FSharp.Collections;
using Factory.Harness.Registry;
using Factory.Integration.Tests.Support;
using Xunit;

namespace Factory.Integration.Tests.Registry;

public class RegistryLedgerProjectorTests
{
    private const string RunId = "run-1";
    private static BoundDecision Decision(string id, string check) => new(DecisionId.NewDecisionId(id), "statement",
        ListModule.Empty<ElementId>(), ListModule.OfSeq([new DecisionCheck("p", check)]));

    private static readonly HandoverRef Handover = new("20261007-c4d072ba",
        [Decision("D-0001", "p check -tc tcOptimistic"), Decision("D-0002", "p check -tc tcDedupe")]);
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeMilliseconds(1_791_400_000_000);

    private static LedgerEvent At(int second, LedgerEvent e) => e with { Ts = T0.AddSeconds(second), TraceId = null };

    /// A finished run: T-001 passed, T-002 blocked on a needs-decision, one decision check per outcome.
    private static List<LedgerEvent> Run() =>
    [
        At(1, new(RunId, "S2", "plan", LedgerKinds.Plan, Outcome: "drafted")),
        At(2, new(RunId, "S2", "task", LedgerKinds.TaskEvent, TaskId: "T-001", TeamId: "team-1", Outcome: "Running")),
        At(3, new(RunId, "S2", "act", LedgerKinds.Turn, TaskId: "T-001", TeamId: "team-1", Model: "Standard", TokensIn: 100, TokensOut: 20, Payload: """{"model_id":"mid-model"}""")),
        At(4, new(RunId, "S2", "act", LedgerKinds.Turn, TaskId: "T-001", TeamId: "team-1", Model: "Frontier", TokensIn: 300, TokensOut: 40, Payload: """{"model_id":"big-model","cache_read":200,"cache_write":50}""")),
        At(5, new(RunId, "S2", "task", LedgerKinds.TaskEvent, TaskId: "T-001", TeamId: "team-1", Element: "Pricing Platform/Referral Worker", Outcome: "Passed")),
        At(6, new(RunId, "S2", "task", LedgerKinds.TaskEvent, TaskId: "T-002", TeamId: "team-2", Outcome: "Running")),
        At(7, new(RunId, "S2", "act", LedgerKinds.Turn, TaskId: "T-002", TeamId: "team-2", Model: "Small", TokensIn: 50, TokensOut: 5)),
        At(8, new(RunId, "S2", "task", LedgerKinds.TaskEvent, TaskId: "T-002", TeamId: "team-2", Outcome: "Blocked")),
        At(9, new(RunId, "S2", "wave", LedgerKinds.NeedsDecision, TaskId: "T-002", TeamId: "team-2", Outcome: "raised",
            Payload: """{"id":"ND-0004","Question":"Dedupe on message id?","Elements":["Pricing Platform/Referral Worker"]}""")),
        At(10, new(RunId, "S2", "integration_verify", LedgerKinds.Verify, Decision: "D-0001", Outcome: "passed",
            Payload: """{"name":"D-0001 [p]","command":"p check -tc tcOptimistic","ExitCode":0,"ms":4300}""")),
        At(11, new(RunId, "S2", "integration_verify", LedgerKinds.Verify, Decision: "D-0002", Outcome: "failed",
            Payload: """{"name":"D-0002 [p]","command":"p check -tc tcDedupe","ExitCode":1,"ms":900}""")),
        At(12, new(RunId, "S2", "integration_verify", LedgerKinds.Verify, Outcome: "passed",
            Payload: """{"name":"integration tests","command":"dotnet test","ExitCode":0}""")),
        At(13, new(RunId, "S2", "quality", LedgerKinds.AsBuiltEdge, TaskId: "T-001", TeamId: "team-1", Outcome: "debt",
            Payload: """{"from":"api","to":"db"}""")),
        At(14, new(RunId, "S2", "quality", LedgerKinds.AsBuiltEdge, TaskId: "T-001", TeamId: "team-1", Outcome: "debt",
            Payload: """{"from":"api","to":"db"}""")),
        At(15, new(RunId, "S2", "quality", LedgerKinds.AsBuiltEdge, TaskId: "T-001", TeamId: "team-1", Outcome: "debt",
            Payload: """{"from":"web","to":"db"}""")),
    ];

    private static Dictionary<string, JsonElement> Row(RegistryEvent e) =>
        JsonDocument.Parse(e.Line).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    [Fact]
    public void Two_projections_of_the_same_run_are_byte_identical()
    {
        var first = RegistryLedgerProjector.Project(Run(), Handover);
        var second = RegistryLedgerProjector.Project(Run(), Handover);

        Assert.NotEmpty(first);
        Assert.Equal(first.Select(e => e.Stream + "\n" + e.Line), second.Select(e => e.Stream + "\n" + e.Line));
    }

    [Fact]
    public void Every_row_validates_against_the_designc_schema_is_kept_by_its_gate_and_lands_in_its_kinds_stream()
    {
        foreach (var e in RegistryLedgerProjector.Project(Run(), Handover))
        {
            Assert.Empty(DesigncSchema.Validate(e.Line));
            Assert.True(DesigncSchema.KeptByGate(e.Line), e.Line);
            Assert.Equal(DesigncSchema.StreamOf(Row(e)["kind"].GetString()!), e.Stream);
        }
    }

    /// pilot P1: the planner's tokens sat on a run-level row no task row counted, so designc showed a task as cheaper
    /// than the budget report said. The run's plan tokens are split over its tasks, the first taking the remainder.
    [Fact]
    public void The_planners_tokens_are_shared_over_the_task_rows()
    {
        var run = Run();
        run.Add(At(0, new(RunId, "S2", "plan", LedgerKinds.Plan, Outcome: "drafted", Model: "frontier", TokensIn: 1000, TokensOut: 100)));
        run.Add(At(1, new(RunId, "S2", "plan", LedgerKinds.Plan, Outcome: "drafted", Model: "frontier", TokensIn: 501, TokensOut: 50)));

        var rows = RegistryLedgerProjector.Project(run, Handover).Select(Row).Where(r => r["kind"].GetString() == "task").ToList();

        Assert.Equal(400 + 751, rows.Single(r => r["task"].GetString() == "T-001")["tokens_in"].GetInt64());
        Assert.Equal(50 + 750, rows.Single(r => r["task"].GetString() == "T-002")["tokens_in"].GetInt64());
        Assert.Equal(60 + 75, rows.Single(r => r["task"].GetString() == "T-001")["tokens_out"].GetInt64());
        Assert.Equal(5 + 75, rows.Single(r => r["task"].GetString() == "T-002")["tokens_out"].GetInt64());
    }

    /// designc's Build view shows spend by role: the task-planner turns by their stage, acting turns by the role in their payload,
    /// and the planner's shared tokens as their own entry (its turns counted once, on the first task).
    [Fact]
    public void Each_task_row_splits_its_tokens_by_role()
    {
        var run = new List<LedgerEvent>
        {
            At(0, new(RunId, "S2", "plan", LedgerKinds.Plan, Outcome: "drafted", Model: "frontier", TokensIn: 1001, TokensOut: 100)),
            At(1, new(RunId, "S2", "task", LedgerKinds.TaskEvent, TaskId: "T-001", TeamId: "team-1", Outcome: "Running")),
            At(2, new(RunId, "S2", "task_plan", LedgerKinds.Turn, TaskId: "T-001", TeamId: "team-1", Model: "Frontier", TokensIn: 200, TokensOut: 20)),
            At(3, new(RunId, "S2", "act", LedgerKinds.Turn, TaskId: "T-001", TeamId: "team-1", Model: "Standard", TokensIn: 300, TokensOut: 30, Payload: """{"role":"s2-test-author"}""")),
            At(4, new(RunId, "S2", "act", LedgerKinds.Turn, TaskId: "T-001", TeamId: "team-1", Model: "Standard", TokensIn: 100, TokensOut: 10, Payload: """{"role":"s2-test-author"}""")),
            At(5, new(RunId, "S2", "task", LedgerKinds.TaskEvent, TaskId: "T-001", TeamId: "team-1", Outcome: "Passed")),
            At(6, new(RunId, "S2", "task", LedgerKinds.TaskEvent, TaskId: "T-002", TeamId: "team-2", Outcome: "Running")),
            At(7, new(RunId, "S2", "task", LedgerKinds.TaskEvent, TaskId: "T-002", TeamId: "team-2", Outcome: "Passed")),
        };

        var rows = RegistryLedgerProjector.Project(run, Handover).Select(Row).Where(r => r["kind"].GetString() == "task").ToList();

        Assert.Equal("""[{"role":"s2-planner","turns":1,"tokens_in":501,"tokens_out":50},{"role":"s2-task-planner","turns":1,"tokens_in":200,"tokens_out":20},{"role":"s2-test-author","turns":2,"tokens_in":400,"tokens_out":40}]""",
            rows.Single(r => r["task"].GetString() == "T-001")["by_role"].GetRawText());
        Assert.Equal("""[{"role":"s2-planner","turns":0,"tokens_in":500,"tokens_out":50}]""",
            rows.Single(r => r["task"].GetString() == "T-002")["by_role"].GetRawText());
    }

    [Fact]
    public void Each_element_pair_the_code_crosses_without_a_relationship_is_one_asbuilt_edge_row_per_run()
    {
        var edges = RegistryLedgerProjector.Project(Run(), Handover).Where(e => Row(e)["kind"].GetString() == "asbuilt_edge").ToList();

        Assert.Equal(["api->db", "web->db"], edges.Select(e => Row(e)["from"].GetString() + "->" + Row(e)["to"].GetString()));
        Assert.All(edges, e => Assert.Equal("pipeline.jsonl", e.Stream));
        Assert.Equal(T0.AddSeconds(13).ToUnixTimeMilliseconds(), Row(edges[0])["ts"].GetInt64());
    }

    [Fact]
    public void Tasks_needs_decisions_and_decision_checks_are_projected()
    {
        var rows = RegistryLedgerProjector.Project(Run(), Handover).Select(Row).ToList();

        var t1 = Assert.Single(rows, r => r["kind"].GetString() == "task" && r["task"].GetString() == "T-001");
        Assert.Equal("passed", t1["status"].GetString());
        Assert.Equal(2, t1["turns"].GetInt32());
        Assert.Equal(400, t1["tokens_in"].GetInt64());
        Assert.Equal(60, t1["tokens_out"].GetInt64());
        Assert.Equal("Frontier", t1["tier"].GetString());
        // usage: per tier and model, in the order first used, so designc shows the split; cache counts only where a turn had any.
        Assert.Equal("""[{"model":"mid-model","tier":"Standard","turns":1,"tokens_in":100,"tokens_out":20},{"model":"big-model","tier":"Frontier","turns":1,"tokens_in":300,"tokens_out":40,"cache_read":200,"cache_write":50}]""",
            t1["usage"].GetRawText());
        Assert.Equal(Handover.Id, t1["handover"].GetString());
        Assert.Equal("Pricing Platform/Referral Worker", t1["element"].GetString());
        Assert.Equal(T0.AddSeconds(5).ToUnixTimeMilliseconds(), t1["ts"].GetInt64());

        var t2 = Assert.Single(rows, r => r["kind"].GetString() == "task" && r["task"].GetString() == "T-002");
        Assert.Equal("blocked", t2["status"].GetString());
        Assert.False(t2.ContainsKey("element"));
        Assert.Equal("""[{"model":"Small","tier":"Small","turns":1,"tokens_in":50,"tokens_out":5}]""", t2["usage"].GetRawText()); // a turn row from before model_id names its tier

        var nd = Assert.Single(rows, r => r["kind"].GetString() == "needs_decision");
        Assert.Equal("ND-0004", nd["id"].GetString());
        Assert.Equal("open", nd["status"].GetString());
        Assert.Equal("Dedupe on message id?", nd["question"].GetString());
        Assert.Equal("T-002", nd["task"].GetString());
        Assert.Equal("Pricing Platform/Referral Worker", nd["element"].GetString());

        var checks = rows.Where(r => r["kind"].GetString() == "check").ToList();
        Assert.Equal(["D-0001", "D-0002"], checks.Select(c => c["decision"].GetString()));
        Assert.Equal(["pass", "fail"], checks.Select(c => c["outcome"].GetString()));
        Assert.Equal(["p check -tc tcOptimistic", "p check -tc tcDedupe"], checks.Select(c => c["check"].GetString()));
        Assert.All(checks, c => Assert.Equal("pipeline", c["source"].GetString()));
        Assert.Equal(1, checks[1]["exit_code"].GetInt32());
    }

    [Fact]
    public void A_rows_event_id_depends_only_on_its_content()
    {
        var before = RegistryLedgerProjector.Project(Run(), Handover);
        var later = Run();
        later.Add(At(20, new(RunId, "S2", "task", LedgerKinds.TaskEvent, TaskId: "T-002", TeamId: "team-2", Outcome: "Passed")));
        var after = RegistryLedgerProjector.Project(later, Handover);

        Assert.Equal(before.Count, before.Select(e => e.EventId).Distinct().Count());
        var unchanged = before.Where(e => !e.Line.Contains("\"T-002\"") || e.Line.Contains("needs_decision")).ToList();
        Assert.All(unchanged, e => Assert.Contains(after, a => a.EventId == e.EventId && a.Line == e.Line));
        Assert.DoesNotContain(after, a => a.Line.Contains("\"task\":\"T-002\"") && a.Line.Contains("\"blocked\""));
    }

    [Fact]
    public async Task Writing_twice_appends_each_row_once()
    {
        var registry = Path.Combine(Path.GetTempPath(), "s2-registry-" + Guid.NewGuid().ToString("N"));
        var events = RegistryLedgerProjector.Project(Run(), Handover);

        await RegistryLedgerWriter.AppendAsync(registry, events, CancellationToken.None);
        await RegistryLedgerWriter.AppendAsync(registry, events, CancellationToken.None);

        var pipeline = await File.ReadAllLinesAsync(Path.Combine(registry, "ledger", "pipeline.jsonl"));
        var checks = await File.ReadAllLinesAsync(Path.Combine(registry, "ledger", "checks.jsonl"));
        Assert.Equal(events.Where(e => e.Stream == "pipeline.jsonl").Select(e => e.Line), pipeline);
        Assert.Equal(events.Where(e => e.Stream == "checks.jsonl").Select(e => e.Line), checks);
    }

    private static LedgerEvent Finished(int second, string status) => At(second, new(RunId, "S2", "finalize", LedgerKinds.Run, Outcome: status));

    private static LedgerEvent D2Passes(int second) => At(second, new(RunId, "S2", "integration_verify", LedgerKinds.Verify, Decision: "D-0002",
        Outcome: "passed", Payload: """{"command":"p check -tc tcDedupe","ExitCode":0}"""));

    private static string? Closed(List<LedgerEvent> run)
    {
        var rows = RegistryLedgerProjector.Project(run, Handover).Where(e => e.Line.Contains("\"kind\":\"handover_closed\"")).ToList();
        Assert.All(rows, r => { Assert.Equal("designc.jsonl", r.Stream); Assert.True(DesigncSchema.KeptByGate(r.Line), r.Line); });
        return rows.Count == 0 ? null : JsonDocument.Parse(Assert.Single(rows).Line).RootElement.GetProperty("outcome").GetString();
    }

    [Fact]
    public void A_merged_run_whose_every_decision_check_passed_closes_the_handover_delivered()
    {
        Assert.Equal("delivered", Closed([.. Run(), D2Passes(13), Finished(14, "merged")]));
    }

    [Fact]
    public void A_merged_run_with_a_decision_check_not_passing_closes_the_handover_abandoned()
    {
        Assert.Equal("abandoned", Closed([.. Run(), Finished(14, "merged")]));                    // D-0002 failed
    }

    /// A run that did not merge can be resumed with --run-id, so only a person abandons its handover (designc Build).
    [Fact]
    public void A_run_that_did_not_merge_leaves_the_handover_open()
    {
        Assert.Null(Closed(Run()));
        foreach (var status in new[] { "parked", "error", "failed", "merge_blocked", "integration_failed", "refused" })
            Assert.Null(Closed([.. Run(), D2Passes(13), Finished(14, status)]));
    }

    private static LedgerEvent Started(int second) => At(second, new(RunId, "S2", "start", LedgerKinds.RunStarted));

    private static LedgerEvent Ended(int second, string status, string detail) => At(second, new(RunId, "S2", "finalize", LedgerKinds.Run,
        Outcome: status, Payload: JsonSerializer.Serialize(new { RunId, Status = status, Detail = detail })));

    private static List<Dictionary<string, JsonElement>> Rows(List<LedgerEvent> run, string kind)
    {
        var rows = RegistryLedgerProjector.Project(run, Handover);
        foreach (var e in rows) Assert.Empty(DesigncSchema.Validate(e.Line));
        Assert.All(rows.Where(e => e.Line.Contains($"\"kind\":\"{kind}\"")), e => Assert.Equal(DesigncSchema.StreamOf(kind), e.Stream));
        return rows.Select(Row).Where(r => r["kind"].GetString() == kind).ToList();
    }

    [Fact]
    public void Every_attempt_start_is_a_run_started_row_and_every_finalize_a_run_ended_row()
    {
        var run = new List<LedgerEvent> { Started(0) };
        run.AddRange(Run());
        run.Add(Ended(16, "parked", "waiting on ND-0004"));
        run.Add(Started(20));
        run.Add(Ended(30, "merged", "PR #12 merged"));

        var starts = Rows(run, "run_started");
        Assert.Equal([T0.ToUnixTimeMilliseconds(), T0.AddSeconds(20).ToUnixTimeMilliseconds()], starts.Select(r => r["ts"].GetInt64()));
        Assert.All(starts, r => { Assert.Equal(RunId, r["run"].GetString()); Assert.Equal(Handover.Id, r["handover"].GetString()); });

        var ends = Rows(run, "run_ended");
        Assert.Equal(["parked", "succeeded"], ends.Select(r => r["status"].GetString()));
        Assert.Equal("waiting on ND-0004", ends[0]["reason"].GetString());
        Assert.False(ends[1].ContainsKey("reason"));
        Assert.All(ends, r => Assert.Equal(RunId, r["run"].GetString()));
    }

    [Fact]
    public void A_run_that_ends_any_other_way_is_failed_with_its_own_status_in_the_reason_and_an_aborted_attempt_ends_it_too()
    {
        var rerun = At(17, new(RunId, "S2", "rerun", LedgerKinds.Run, TaskId: "T-001", Outcome: "rerun"));
        var aborted = At(25, new(RunId, "S2", "host", LedgerKinds.RunAborted, Outcome: "cancelled",
            Payload: JsonSerializer.Serialize(new { status = "cancelled", detail = "The operation was canceled." })));
        var crashed = At(35, new(RunId, "S2", "host", LedgerKinds.RunAborted, Outcome: "failed",
            Payload: JsonSerializer.Serialize(new { status = "failed", detail = "HttpRequestException: 400" })));

        var ends = Rows([Started(0), Ended(16, "plan_rejected", "planner did not produce a valid plan"), rerun, Started(20), aborted, Started(30), crashed], "run_ended");

        Assert.Equal(["failed", "cancelled", "failed"], ends.Select(r => r["status"].GetString()));
        Assert.Equal("plan_rejected: planner did not produce a valid plan", ends[0]["reason"].GetString());
        Assert.Equal("The operation was canceled.", ends[1]["reason"].GetString());
        Assert.Equal("failed: HttpRequestException: 400", ends[2]["reason"].GetString());
    }

    [Fact]
    public void A_task_row_names_its_run_so_designc_counts_only_the_latest_as_spend()
    {
        Assert.All(Rows(Run(), "task"), r => Assert.Equal(RunId, r["run"].GetString()));
    }
}
