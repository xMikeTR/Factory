using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Harness.Sandbox;

namespace Factory.S2.Verify;

/// Runs verify entries exactly as the plan declared them. The agent never runs these on its own
/// behalf and cannot edit them: the plan is frozen, and verify scripts sit under protected paths.
public sealed class VerifyRunner(ILedger ledger, ISandboxFactory sandboxes)
{
    /// Automated entries run in order. Cleanup always runs, pass or fail, and a failing cleanup
    /// counts as a failed outcome: leftover seeded state would make the next run lie.
    /// Manual entries are logged as pending; they gate the merge tier, not the task.
    public async Task<List<VerifyOutcome>> RunTaskAsync(
        string runId, string teamId, PlannedTask task, int attempt, string worktree, CancellationToken ct)
    {
        var taskId = task.Id.Value;
        var env = new Dictionary<string, string>
        {
            ["FACTORY_RUN_ID"] = runId,
            ["FACTORY_TASK_ID"] = taskId,
            ["FACTORY_VERIFY_NS"] = $"{runId}-{taskId}-{attempt}",
        };
        await using var sandbox = await sandboxes.ForTeamAsync(teamId, worktree, ct);
        var outcomes = new List<VerifyOutcome>();

        foreach (var entry in task.Verify)
        {
            switch (entry)
            {
                case VerifyEntry.Automated { Item: var a }:
                    try
                    {
                        outcomes.Add(ToOutcome(a.Name, await sandbox.RunAsync(a.Run, worktree, TimeSpan.FromSeconds(a.TimeoutSeconds), env, ct)));
                    }
                    finally
                    {
                        if (a.Cleanup?.Value is { } cleanup)
                        {
                            var c = await sandbox.RunAsync(cleanup, worktree, TimeSpan.FromSeconds(a.TimeoutSeconds), env, CancellationToken.None);
                            if (!c.Succeeded) outcomes.Add(ToOutcome($"cleanup: {a.Name}", c));
                        }
                    }
                    break;
                case VerifyEntry.Manual { Item: var m }:
                    await ledger.AppendAsync(new LedgerEvent(runId, "S2", "verify", LedgerKinds.Verify, TaskId: taskId, TeamId: teamId,
                        Outcome: "manual_pending", Payload: JsonSerializer.Serialize(new { attempt, name = m.Name, m.Instructions })), ct);
                    break;
            }
        }

        var passed = outcomes.Count > 0 && outcomes.All(o => o.ExitCode == 0);
        await ledger.AppendAsync(new LedgerEvent(runId, "S2", "verify", LedgerKinds.Verify, TaskId: taskId, TeamId: teamId,
            Outcome: passed ? "passed" : "failed",
            Payload: JsonSerializer.Serialize(new
            {
                attempt,
                sandbox = sandbox.Kind,
                outcomes = outcomes.Select(o => new { o.Name, o.ExitCode, o.DurationMs }),
            })), ct);
        return outcomes;
    }

    /// Commands that are not tied to one task: decision checks from the registry, the full
    /// test suite on the integration branch, PObserve over the resulting logs.
    public async Task<VerifyOutcome> RunCommandAsync(string runId, string name, string command, string workDir, TimeSpan timeout, CancellationToken ct)
    {
        await using var sandbox = await sandboxes.ForTeamAsync("integration", workDir, ct);
        var r = await sandbox.RunAsync(command, workDir, timeout, new Dictionary<string, string> { ["FACTORY_RUN_ID"] = runId }, ct);
        var outcome = ToOutcome(name, r);
        await ledger.AppendAsync(new LedgerEvent(runId, "S2", "integration_verify", LedgerKinds.Verify,
            Outcome: r.Succeeded ? "passed" : "failed",
            Payload: JsonSerializer.Serialize(new { name, command, r.ExitCode, sandbox = sandbox.Kind, tail = r.Tail(2000) })), ct);
        return outcome;
    }

    private static VerifyOutcome ToOutcome(string name, ProcessResult r) =>
        new(name, r.TimedOut ? 124 : r.ExitCode, (long)r.Duration.TotalMilliseconds, r.Tail());
}
