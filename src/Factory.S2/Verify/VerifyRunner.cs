using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Harness.Sandbox;
using Factory.S2.Git;

namespace Factory.S2.Verify;

using Core = Factory.Core.Core;

/// Runs verify entries exactly as the plan declared them. The agent never runs these on its own
/// behalf and cannot edit them: the plan is frozen, and verify scripts sit under protected paths.
public sealed class VerifyRunner(ILedger ledger, ISandboxFactory sandboxes, GitWorkspace git)
{
    /// Automated entries run in order, each against the tree as it found it: whatever a check
    /// writes (generated code, logs) is reverted before the next one starts, so one check cannot
    /// break another. Cleanup always runs, pass or fail, and a failing cleanup counts as a failed
    /// outcome: leftover seeded state would make the next run lie.
    /// Manual entries are logged as pending; they gate the merge tier, not the task.
    /// A refused restore (RestoreGate) runs nothing: the single outcome carries the gate's reasons.
    /// `stage` labels the ledger row: "verify" (green run), "red_run" or "stash_check".
    public async Task<List<VerifyOutcome>> RunTaskAsync(
        string runId, string teamId, PlannedTask task, int attempt, string worktree, CancellationToken ct, string stage = "verify")
    {
        var taskId = task.Id.Value;
        var env = new Dictionary<string, string>
        {
            ["FACTORY_RUN_ID"] = runId,
            ["FACTORY_TASK_ID"] = taskId,
            ["FACTORY_VERIFY_NS"] = stage == "verify" ? $"{runId}-{taskId}-{attempt}" : $"{runId}-{taskId}-{attempt}-{stage}",
        };
        await using var sandbox = await sandboxes.ForTeamAsync(teamId, worktree, ct);
        var outcomes = new List<VerifyOutcome>();
        if (sandbox.RestoreRefusal is { } refusal) outcomes.Add(RestoreRefusal.outcome(Core.ToList(refusal)));

        foreach (var entry in sandbox.RestoreRefusal is null ? task.Verify : Enumerable.Empty<VerifyEntry>())
        {
            switch (entry)
            {
                case VerifyEntry.Automated { Item: var a }:
                    await using (await git.LeaveAsFoundAsync(worktree, ct))
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
                case VerifyEntry.Manual { Item: var m } when stage == "verify":
                    await ledger.AppendAsync(new LedgerEvent(runId, "S2", "verify", LedgerKinds.Verify, TaskId: taskId, TeamId: teamId,
                        Outcome: "manual_pending", Payload: JsonSerializer.Serialize(new { attempt, name = m.Name, m.Instructions })), ct);
                    break;
                case VerifyEntry.Manual:
                    break;
            }
        }

        var passed = outcomes.Count > 0 && outcomes.All(o => o.ExitCode == 0);
        await ledger.AppendAsync(new LedgerEvent(runId, "S2", stage, LedgerKinds.Verify, TaskId: taskId, TeamId: teamId,
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
    /// `decision` is set for a bound decision's check, so the row projects to designc's checks.jsonl.
    public async Task<VerifyOutcome> RunCommandAsync(string runId, string name, string command, string workDir, TimeSpan timeout, CancellationToken ct,
        string? decision = null)
    {
        await using var sandbox = await sandboxes.ForTeamAsync("integration", workDir, ct);
        var r = await sandbox.RunAsync(command, workDir, timeout, new Dictionary<string, string> { ["FACTORY_RUN_ID"] = runId }, ct);
        var outcome = ToOutcome(name, r);
        await ledger.AppendAsync(new LedgerEvent(runId, "S2", "integration_verify", LedgerKinds.Verify, Decision: decision,
            Outcome: r.Succeeded ? "passed" : "failed",
            Payload: JsonSerializer.Serialize(new { name, command, ExitCode = outcome.ExitCode, ms = outcome.DurationMs, sandbox = sandbox.Kind, tail = r.Tail(2000) })), ct);
        return outcome;
    }

    /// Where a suite command writes JUnit XML, relative to its working directory. Passed to it as
    /// FACTORY_TEST_RESULTS; removed after reading so it never shows up as a change.
    public const string ResultsPath = ".factory-results/junit.xml";

    private static readonly TimeSpan SuiteTimeout = TimeSpan.FromMinutes(30);

    /// The P2P baseline for a team: the suite's results on the team branch before its first task.
    /// Recorded once in the ledger and reused on resume. Null when the suite writes no JUnit XML,
    /// which turns the regression check off.
    public async Task<IReadOnlyList<TestResult>?> BaselineAsync(string runId, string teamId, string command, string worktree, CancellationToken ct)
    {
        await foreach (var e in ledger.ReadAsync(runId, ct))
            if (e is { Kind: LedgerKinds.Verify, Stage: "baseline" } && e.TeamId == teamId && e.Payload is not null)
                return DecodeTests(e.Payload);

        var (tests, _) = await SuiteAsync(runId, teamId, command, worktree, ct);
        await ledger.AppendAsync(new LedgerEvent(runId, "S2", "baseline", LedgerKinds.Verify, TeamId: teamId,
            Outcome: tests is null ? "no_results" : "recorded",
            Payload: JsonSerializer.Serialize(new { tests = tests?.Select(t => new { name = t.Name, passed = t.Passed }) })), ct);
        return tests;
    }

    /// Run the suite after a green run and compare with the baseline. Null when nothing that
    /// passed before fails now; otherwise a failing outcome naming the regressions, which joins
    /// the green outcomes (so Passed is refused) and the next brief.
    public async Task<VerifyOutcome?> RegressionAsync(
        string runId, string teamId, PlannedTask task, IReadOnlyList<TestResult> baseline, string command, string worktree, CancellationToken ct)
    {
        var (current, r) = await SuiteAsync(runId, teamId, command, worktree, ct);
        var names = Regression.regressions(Core.ToList(baseline), Core.ToList(current ?? [])).ToList();
        await ledger.AppendAsync(new LedgerEvent(runId, "S2", "regression", LedgerKinds.Verify, TaskId: task.Id.Value, TeamId: teamId,
            Outcome: names.Count == 0 ? "passed" : "failed",
            Payload: JsonSerializer.Serialize(new { regressions = names, results = current is not null })), ct);
        if (names.Count == 0) return null;
        var tail = "These tests passed before this task and fail now:\n" + string.Join("\n", names.Select(n => $"- {n}"))
            + (current is null ? "\n\nThe suite wrote no results (did it build?):\n" + r.Tail() : "");
        return new VerifyOutcome("regressions", 1, (long)r.Duration.TotalMilliseconds, tail);
    }

    private async Task<(List<TestResult>? Tests, ProcessResult Run)> SuiteAsync(string runId, string teamId, string command, string worktree, CancellationToken ct)
    {
        var results = Path.Combine(worktree, ResultsPath);
        var dir = Path.GetDirectoryName(results)!;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        try
        {
            await using var sandbox = await sandboxes.ForTeamAsync(teamId, worktree, ct);
            var r = await sandbox.RunAsync(command, worktree, SuiteTimeout,
                new Dictionary<string, string> { ["FACTORY_RUN_ID"] = runId, ["FACTORY_TEST_RESULTS"] = ResultsPath }, ct);
            return (File.Exists(results) ? JUnit.Parse(await File.ReadAllTextAsync(results, ct)) : null, r);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private static List<TestResult>? DecodeTests(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("tests", out var tests) || tests.ValueKind != JsonValueKind.Array) return null;
        return tests.EnumerateArray()
            .Select(t => new TestResult(t.GetProperty("name").GetString()!, t.GetProperty("passed").GetBoolean()))
            .ToList();
    }

    private static VerifyOutcome ToOutcome(string name, ProcessResult r) =>
        new(name, r.TimedOut ? 124 : r.ExitCode, (long)r.Duration.TotalMilliseconds, r.Tail());
}
