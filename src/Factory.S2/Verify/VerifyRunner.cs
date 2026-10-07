using System.Text.Json;
using Factory.Core;
using Factory.Harness;

namespace Factory.S2.Verify;

/// Runs verify entries exactly as the plan declared them. The agent never runs these on its own
/// behalf and cannot edit them: the plan is frozen, and verify scripts sit under protected paths.
public sealed class VerifyRunner(ILedger ledger)
{
    /// Automated entries run in order. Cleanup always runs, pass or fail, and a failing cleanup
    /// counts as a failed outcome: leftover seeded state would make the next run lie.
    /// Manual entries are logged as pending; they gate the merge tier, not the task.
    /// TODO(you) — RESOURCES.md §7.
    ///   env: FACTORY_RUN_ID, FACTORY_TASK_ID, FACTORY_VERIFY_NS = "{runId}-{taskId}-{attempt}"
    ///   for each entry in task.Verify:
    ///     VerifyEntry.Automated { Item: var a } ->
    ///         run a.Run via ProcessRunner.ShellAsync(timeout a.TimeoutSeconds) -> ToOutcome
    ///         cleanup (a.Cleanup?.Value; F# None is null) runs ALWAYS, with CancellationToken.None,
    ///         and a failing cleanup is added as its own failed outcome "cleanup: {name}"
    ///     VerifyEntry.Manual { Item: var m } -> ledger row, Outcome "manual_pending"; no outcome
    ///   one summary ledger row (passed/failed, attempt, names + exit codes); return outcomes
    public async Task<List<VerifyOutcome>> RunTaskAsync(
        string runId, string teamId, PlannedTask task, int attempt, string worktree, CancellationToken ct)
    {
        await Task.CompletedTask;
        throw new NotImplementedException("TODO: VerifyRunner.RunTaskAsync (RESOURCES.md §7)");
    }

    /// Commands that are not tied to one task: decision checks from the registry, the full
    /// test suite on the integration branch, PObserve over the resulting logs.
    public async Task<VerifyOutcome> RunCommandAsync(string runId, string name, string command, string workDir, TimeSpan timeout, CancellationToken ct)
    {
        var r = await ProcessRunner.ShellAsync(command, workDir, timeout, new Dictionary<string, string> { ["FACTORY_RUN_ID"] = runId }, ct);
        var outcome = ToOutcome(name, r);
        await ledger.AppendAsync(new LedgerEvent(runId, "S2", "integration_verify", LedgerKinds.Verify,
            Outcome: r.Succeeded ? "passed" : "failed",
            Payload: JsonSerializer.Serialize(new { name, command, r.ExitCode, tail = r.Tail(2000) })), ct);
        return outcome;
    }

    private static VerifyOutcome ToOutcome(string name, ProcessResult r) =>
        new(name, r.TimedOut ? 124 : r.ExitCode, (long)r.Duration.TotalMilliseconds, r.Tail());
}
