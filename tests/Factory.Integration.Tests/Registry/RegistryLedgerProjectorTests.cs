using System.Text.Json;
using Factory.Harness;
using Factory.Harness.Registry;
using Factory.Integration.Tests.Support;
using Xunit;

namespace Factory.Integration.Tests.Registry;

public class RegistryLedgerProjectorTests
{
    private const string RunId = "run-1";
    private static readonly HandoverRef Handover = new("20261007-c4d072ba");
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeMilliseconds(1_791_400_000_000);

    private static LedgerEvent At(int second, LedgerEvent e) => e with { Ts = T0.AddSeconds(second), TraceId = null };

    /// A finished run: T-001 passed, T-002 blocked on a needs-decision, one decision check per outcome.
    private static List<LedgerEvent> Run() =>
    [
        At(1, new(RunId, "S2", "plan", LedgerKinds.Plan, Outcome: "drafted")),
        At(2, new(RunId, "S2", "task", LedgerKinds.TaskEvent, TaskId: "T-001", TeamId: "team-1", Outcome: "Running")),
        At(3, new(RunId, "S2", "act", LedgerKinds.Turn, TaskId: "T-001", TeamId: "team-1", Model: "Standard", TokensIn: 100, TokensOut: 20)),
        At(4, new(RunId, "S2", "act", LedgerKinds.Turn, TaskId: "T-001", TeamId: "team-1", Model: "Frontier", TokensIn: 300, TokensOut: 40)),
        At(5, new(RunId, "S2", "task", LedgerKinds.TaskEvent, TaskId: "T-001", TeamId: "team-1", Outcome: "Passed")),
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
    public void Every_row_validates_against_the_designc_schema_and_lands_in_its_kinds_stream()
    {
        foreach (var e in RegistryLedgerProjector.Project(Run(), Handover))
        {
            Assert.Empty(DesigncSchema.Validate(e.Line));
            Assert.Equal(DesigncSchema.StreamOf(Row(e)["kind"].GetString()!), e.Stream);
        }
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
        Assert.Equal(Handover.Id, t1["handover"].GetString());
        Assert.Equal(T0.AddSeconds(5).ToUnixTimeMilliseconds(), t1["ts"].GetInt64());

        Assert.Equal("blocked", Assert.Single(rows, r => r["kind"].GetString() == "task" && r["task"].GetString() == "T-002")["status"].GetString());

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
}
