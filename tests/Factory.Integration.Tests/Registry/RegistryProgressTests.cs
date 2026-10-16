using System.Text.Json;
using Factory.Harness;
using Factory.Harness.Registry;
using Xunit;

namespace Factory.Integration.Tests.Registry;

/// designc's Build view shows a run while it moves: each attempt's start, each task state change and the
/// end reach the registry when they happen, not only at Finalize.
public class RegistryProgressTests : IDisposable
{
    private const string RunId = "run-1";
    private static readonly HandoverRef Handover = new("20261015-65fa159e", []);
    private readonly string _dir = Directory.CreateTempSubdirectory("s2-progress-").FullName;
    private string Registry => Path.Combine(_dir, "registry");
    private readonly JsonlLedger _inner;

    public RegistryProgressTests()
    {
        _inner = new JsonlLedger(Path.Combine(_dir, "state", "ledger.jsonl"));
        Directory.CreateDirectory(Path.Combine(Registry, "ledger"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private RegistryProgressLedger Ledger(Func<string, HandoverRef>? handover = null) => new(_inner, Registry, handover ?? (_ => Handover));

    private List<JsonElement> Pipeline(string kind)
    {
        var path = Path.Combine(Registry, "ledger", "pipeline.jsonl");
        if (!File.Exists(path)) return [];
        return File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement.Clone())
            .Where(r => r.GetProperty("kind").GetString() == kind).ToList();
    }

    private static LedgerEvent Task(string state) => new(RunId, "S2", "task", LedgerKinds.TaskEvent, TaskId: "T-001", TeamId: "team-1", Outcome: state);

    [Fact]
    public async Task An_attempt_start_and_a_task_state_change_reach_the_registry_when_they_happen()
    {
        var ledger = Ledger();
        await ledger.AppendAsync(new(RunId, "S2", "start", LedgerKinds.RunStarted));
        Assert.Equal(RunId, Assert.Single(Pipeline("run_started")).GetProperty("run").GetString());

        await ledger.AppendAsync(Task("Running"));
        await ledger.AppendAsync(new(RunId, "S2", "act", LedgerKinds.Turn, TaskId: "T-001", TeamId: "team-1", TokensIn: 100, TokensOut: 10));
        await ledger.AppendAsync(Task("Passed"));

        Assert.Equal(["running", "passed"], Pipeline("task").Select(r => r.GetProperty("status").GetString()));
        Assert.Equal(100, Pipeline("task")[1].GetProperty("tokens_in").GetInt64());
        Assert.Empty(Pipeline("run_ended"));
    }

    [Fact]
    public async Task Every_task_state_projects_so_a_task_in_review_does_not_stop_the_progress()
    {
        var ledger = Ledger();
        foreach (var state in Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(typeof(Factory.Core.TaskState), null).Select(c => c.Name))
            await ledger.AppendAsync(Task(state));

        Assert.Equal(["running", "running", "blocked", "running", "running", "passed", "failed"],
            Pipeline("task").Select(r => r.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task With_no_registry_folder_or_no_handover_yet_the_event_is_still_logged_and_nothing_is_projected()
    {
        Directory.Delete(Registry, recursive: true);
        await Ledger().AppendAsync(Task("Running"));
        Assert.False(Directory.Exists(Registry));

        Directory.CreateDirectory(Path.Combine(Registry, "ledger"));
        await Ledger(_ => throw new FileNotFoundException("handover.json")).AppendAsync(Task("Passed"));
        Assert.Empty(Pipeline("task"));

        var logged = new List<LedgerEvent>();
        await foreach (var e in _inner.ReadAsync(RunId)) logged.Add(e);
        Assert.Equal(2, logged.Count);
    }

    [Fact]
    public async Task An_attempt_that_ends_without_finalize_is_recorded_once_as_aborted_and_ends_the_run_in_the_registry()
    {
        var ledger = Ledger();
        await ledger.AppendAsync(new(RunId, "S2", "start", LedgerKinds.RunStarted));

        Assert.True(await RunAttempt.EndAsync(ledger, RunId, "cancelled", "The operation was canceled.", CancellationToken.None));
        Assert.False(await RunAttempt.EndAsync(ledger, RunId, "cancelled", "again", CancellationToken.None));

        var end = Assert.Single(Pipeline("run_ended"));
        Assert.Equal("cancelled", end.GetProperty("status").GetString());
        Assert.Equal("The operation was canceled.", end.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task An_attempt_that_reached_finalize_or_never_started_gets_no_aborted_row()
    {
        var ledger = Ledger();
        Assert.False(await RunAttempt.EndAsync(ledger, RunId, "refused", "over budget", CancellationToken.None));

        await ledger.AppendAsync(new(RunId, "S2", "start", LedgerKinds.RunStarted));
        await ledger.AppendAsync(new(RunId, "S2", "finalize", LedgerKinds.Run, Outcome: "parked",
            Payload: JsonSerializer.Serialize(new { RunId, Status = "parked", Detail = "waiting on ND-0001" })));
        Assert.False(await RunAttempt.EndAsync(ledger, RunId, "parked", "waiting on ND-0001", CancellationToken.None));

        Assert.Equal(["parked"], Pipeline("run_ended").Select(r => r.GetProperty("status").GetString()));
    }
}
