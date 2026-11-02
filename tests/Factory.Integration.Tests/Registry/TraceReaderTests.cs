using Factory.Core;
using Factory.Harness;
using Factory.Harness.Registry;
using Factory.Integration.Tests.Support;
using Xunit;

namespace Factory.Integration.Tests.Registry;

public class TraceReaderTests
{
    private const string RunId = "run-1";
    private static readonly Limits Limits = new(5, 3);
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeMilliseconds(1_791_400_000_000);
    private static readonly HandoverRef Handover = new("20261007-c4d072ba", []);

    private static LedgerEvent At(int second, LedgerEvent e, string? traceId = null) => e with { Ts = T0.AddSeconds(second), TraceId = traceId };

    private static LedgerEvent Task(int second, string task, TaskEvent ev, string outcome) =>
        At(second, new(RunId, "S2", "task", LedgerKinds.TaskEvent, TaskId: task, TeamId: "team-1", Outcome: outcome, Payload: Factory.Core.Core.EncodeEvent(ev)));

    private static LedgerEvent Decide(int second, string task, string kind) =>
        At(second, new(RunId, "S2", "configurator", LedgerKinds.Configurator, TaskId: task, Outcome: kind, Model: "Standard"));

    private static LedgerEvent Turn(int second, string task, long tin, long tout, string? payload) =>
        At(second, new(RunId, "S2", "act", LedgerKinds.Turn, TaskId: task, TeamId: "team-1", Model: "Standard", TokensIn: tin, TokensOut: tout, Payload: payload));

    private static NeedsDecision Question(string source) =>
        new("Dedupe on message id?", Microsoft.FSharp.Collections.ListModule.Empty<ElementId>(), source,
            Microsoft.FSharp.Collections.ListModule.Empty<DecisionOption>(), Microsoft.FSharp.Core.FSharpOption<string>.None);

    /// T-001 stalls (escalated, then abandoned); T-002 parks on the agent's question; T-003 is still running.
    private static List<LedgerEvent> Run() =>
    [
        At(0, new(RunId, "S2", "start", LedgerKinds.RunStarted,
            Payload: """{"procedures":[{"id":"prompt:act","version":"1","hash":"sha256:aa"},{"id":"policy:jira-gate","version":"1","hash":"sha256:bb"}]}""")),
        At(1, new(RunId, "S2", "plan", LedgerKinds.Plan, Outcome: "drafted", TokensIn: 11, TokensOut: 3)),
        Decide(2, "T-001", "Act"),
        Task(3, "T-001", TaskEvent.NewTurnStarted(ModelTier.Standard), "Running"),
        Turn(4, "T-001", 100, 10, """{"model_id":"claude-a","tool_calls":4}"""),
        Task(5, "T-001", TaskEvent.TurnEnded, "Running"),
        At(6, new(RunId, "S2", "act", LedgerKinds.ScopeViolation, TaskId: "T-001", Outcome: "reverted")),
        Decide(7, "T-001", "Escalate"),
        At(8, new(RunId, "S2", "task", LedgerKinds.TaskEvent, TaskId: "T-001", Outcome: "Failed",
            Payload: Factory.Core.Core.EncodeEvent(TaskEvent.NewAbandon("not making progress"))), traceId: "trace-1"),
        Decide(9, "T-002", "Act"),
        Task(10, "T-002", TaskEvent.NewTurnStarted(ModelTier.Standard), "Running"),
        Turn(11, "T-002", 50, 5, null),
        Task(12, "T-002", TaskEvent.NewNeedsDecisionRaised(Question("agent")), "Blocked"),
        Task(13, "T-003", TaskEvent.NewTurnStarted(ModelTier.Standard), "Running"),
    ];

    private static TaskTrace Of(IReadOnlyList<TaskTrace> traces, string task) => Assert.Single(traces, t => t.Task == task);

    [Fact]
    public void A_task_still_in_flight_has_no_trace()
    {
        var traces = TraceReader.Read(Run(), Limits);

        Assert.Equal(["T-001", "T-002"], traces.Select(t => t.Task));
    }

    [Fact]
    public void An_abandon_right_after_an_escalate_is_a_stall_and_counts_what_the_task_did()
    {
        var t = Of(TraceReader.Read(Run(), Limits), "T-001");

        Assert.True(t.End.Outcome.IsTraceFailed);
        Assert.Equal("stalled", Factory.Core.Core.CauseToLedger(t.End.Cause.Value));
        Assert.Equal(["claude-a"], t.Models);
        Assert.Equal(new TraceActions(1, 4, 0, 0, 1), t.Actions);
        Assert.Equal("trace-1", t.TraceId.Value);
    }

    [Fact]
    public void An_agent_question_parks_the_task_and_a_turn_with_no_model_id_is_named_by_its_tier()
    {
        var t = Of(TraceReader.Read(Run(), Limits), "T-002");

        Assert.True(t.End.Outcome.IsTraceBlocked);
        Assert.Equal("parked", Factory.Core.Core.CauseToLedger(t.End.Cause.Value));
        Assert.Equal(["Standard"], t.Models);
        Assert.Equal(0, t.Actions.ToolCalls);
    }

    [Fact]
    public void An_abandon_with_no_escalate_before_it_is_a_persons_abandon()
    {
        var run = Run();
        run.RemoveAll(e => e.Kind == LedgerKinds.Configurator && e.Outcome == "Escalate");

        Assert.Equal("abandoned", Factory.Core.Core.CauseToLedger(Of(TraceReader.Read(run, Limits), "T-001").End.Cause.Value));
    }

    [Fact]
    public void Tokens_match_the_projected_task_rows_planner_share_included()
    {
        var run = Run();
        var traces = TraceReader.Read(run, Limits);
        var rows = RegistryLedgerProjector.Project(run, Handover, Limits)
            .Where(r => r.Line.Contains("\"kind\":\"task\""))
            .Select(r => System.Text.Json.JsonDocument.Parse(r.Line).RootElement)
            .ToDictionary(r => r.GetProperty("task").GetString()!);

        foreach (var t in traces)
        {
            Assert.Equal(rows[t.Task].GetProperty("tokens_in").GetInt64(), t.TokensIn);
            Assert.Equal(rows[t.Task].GetProperty("tokens_out").GetInt64(), t.TokensOut);
        }
    }

    [Fact]
    public void Procedures_come_from_the_latest_run_started_and_no_bundle_is_recorded_as_none()
    {
        var run = Run();
        run.Add(At(20, new(RunId, "S2", "start", LedgerKinds.RunStarted,
            Payload: """{"procedures":[{"id":"prompt:act","version":"2","hash":"sha256:cc"}]}""")));

        var t = Of(TraceReader.Read(run, Limits, jira: "PRJ-7"), "T-001");

        Assert.Equal([new ProcedureRef("prompt:act", "2", "sha256:cc")], t.Procedures);
        Assert.True(Microsoft.FSharp.Core.FSharpOption<string>.get_IsNone(t.BundleId));
        Assert.Equal("PRJ-7", t.Jira.Value);
    }

    [Fact]
    public void A_run_with_no_procedures_and_unreadable_payloads_still_reads()
    {
        var run = Run();
        run[0] = run[0] with { Payload = "{broken" };
        run.Add(At(21, new(RunId, "S2", "act", LedgerKinds.Turn, TaskId: "T-001", Model: "Standard", Payload: "<<")));

        var t = Of(TraceReader.Read(run, Limits), "T-001");

        Assert.Empty(t.Procedures);
        Assert.Equal(2, t.Actions.Turns);
    }

    private static List<System.Text.Json.JsonElement> Projected(List<LedgerEvent> run, string kind) =>
        RegistryLedgerProjector.Project(run, Handover, Limits)
            .Select(r => System.Text.Json.JsonDocument.Parse(r.Line).RootElement)
            .Where(r => r.GetProperty("kind").GetString() == kind).ToList();

    [Fact]
    public void The_projection_writes_one_trace_row_per_ended_task_and_each_validates_against_designc()
    {
        var rows = RegistryLedgerProjector.Project(Run(), Handover, Limits).Where(r => r.Line.Contains("\"kind\":\"trace\"")).ToList();

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Empty(DesigncSchema.Validate(r.Line)));
        Assert.All(rows, r => Assert.Equal("pipeline.jsonl", r.Stream));
    }

    [Fact]
    public void A_trace_row_carries_outcome_cause_no_bundle_procedures_and_its_actions_under_payload()
    {
        var stalled = Assert.Single(Projected(Run(), "trace"), r => r.GetProperty("task").GetString() == "T-001");

        Assert.Equal("failed", stalled.GetProperty("outcome").GetString());
        Assert.Equal("stalled", stalled.GetProperty("cause").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, stalled.GetProperty("bundle_id").ValueKind);
        Assert.Equal(["claude-a"], stalled.GetProperty("models").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal("sha256:aa", stalled.GetProperty("procedures")[0].GetProperty("hash").GetString());
        Assert.Equal("trace-1", stalled.GetProperty("trace_id").GetString());
        var actions = stalled.GetProperty("payload").GetProperty("actions");
        Assert.Equal(1, actions.GetProperty("turns").GetInt32());
        Assert.Equal(4, actions.GetProperty("tool_calls").GetInt32());
        Assert.Equal(1, actions.GetProperty("scope_reverts").GetInt32());
    }

    [Fact]
    public void The_task_row_names_its_cause()
    {
        var tasks = Projected(Run(), "task").ToDictionary(r => r.GetProperty("task").GetString()!);

        Assert.Equal("stalled", tasks["T-001"].GetProperty("cause").GetString());
        Assert.Equal("parked", tasks["T-002"].GetProperty("cause").GetString());
        Assert.False(tasks["T-003"].TryGetProperty("cause", out _));
    }

    [Fact]
    public void A_cause_the_task_rows_status_cannot_carry_is_left_off_it()
    {
        var reviewParked = new TaskEnd(TraceOutcome.TraceBlocked, Microsoft.FSharp.Core.FSharpOption<TraceCause>.Some(TraceCause.NewGateFailed("review")));

        Assert.Null(RegistryLedgerProjector.TaskRowCause("running", reviewParked)); // parked after review shows as running today
        Assert.Equal("gate_failed:review", RegistryLedgerProjector.TaskRowCause("blocked", reviewParked));
        Assert.Null(RegistryLedgerProjector.TaskRowCause("passed", new TaskEnd(TraceOutcome.TracePassed, Microsoft.FSharp.Core.FSharpOption<TraceCause>.None)));
    }
}
