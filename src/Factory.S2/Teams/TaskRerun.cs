using System.Globalization;
using Factory.Core;
using Factory.Harness;

namespace Factory.S2.Teams;

using Core = Factory.Core.Core;

/// One attempt at a task. Outcome is "passed", "failed" or "blocked".
public sealed record RerunAttempt(int Attempt, string RunId, string Outcome, int Turns, long TokensIn, long TokensOut, string? Failure = null);

public sealed record RerunReport(string TaskId, IReadOnlyList<RerunAttempt> Attempts)
{
    public int K => Attempts.Count;
    public int Passed => Attempts.Count(a => a.Outcome == "passed");
    public double PassRate => K == 0 ? 0 : (double)Passed / K;
    public double MeanTurns => K == 0 ? 0 : Attempts.Average(a => a.Turns);
    public double MeanTokensIn => K == 0 ? 0 : Attempts.Average(a => a.TokensIn);
    public double MeanTokensOut => K == 0 ? 0 : Attempts.Average(a => a.TokensOut);

    public string Line() => string.Format(CultureInfo.InvariantCulture,
        "{0} k={1}: passed {2}/{1} ({3:0}%), turns mean {4:0.0}, tokens in/out mean {5:0}/{6:0}",
        TaskId, K, Passed, PassRate * 100, MeanTurns, MeanTokensIn, MeanTokensOut);
}

/// Measures how reliably the agents get one task to Passed: k independent attempts, each its own
/// run (`<prefix>-k<n>`: an integration branch from base and a fresh team worktree), driven by the
/// same task loop as a real run. Nothing is merged into integration and no PR is opened. Tasks the
/// task depends on are not replayed; point the base branch at their work when that matters.
/// Turns and tokens come from the attempt's own ledger rows.
public static class TaskRerun
{
    private static readonly IReadOnlyDictionary<string, DecisionAnswer> NoAnswers = new Dictionary<string, DecisionAnswer>();

    public static async Task<RerunReport> RunAsync(FactoryServices svc, Handover handover, PlannedTask task, int k, string runIdPrefix,
        CancellationToken ct)
    {
        if (k < 1) throw new ArgumentOutOfRangeException(nameof(k), k, "k must be at least 1");
        var attempts = new List<RerunAttempt>();
        for (var n = 1; n <= k; n++)
            attempts.Add(await AttemptAsync(svc, handover, task, n, $"{runIdPrefix}-k{n}", ct));
        return new RerunReport(task.Id.Value, attempts);
    }

    private static async Task<RerunAttempt> AttemptAsync(FactoryServices svc, Handover handover, PlannedTask task, int n, string runId,
        CancellationToken ct)
    {
        var team = new Team(TeamId.NewTeamId("team-" + task.Id.Value), Core.ToList([task]), task.Writes, task.Elements);
        await svc.Git.EnsureIntegrationAsync(runId, svc.Settings.BaseBranch, ct);
        var worktree = await svc.Git.EnsureTeamAsync(runId, team.Id.Value, ct);
        var baseline = await svc.Verify.BaselineAsync(runId, team.Id.Value, svc.Settings.IntegrationTestCommand, worktree, ct);

        var stop = await new TaskLoop(svc, handover, runId, team, task, worktree, baseline).RunAsync(NoAnswers, ct);

        var turns = new List<LedgerEvent>();
        await foreach (var e in svc.Ledger.ReadAsync(runId, ct))
            if (e.Kind == LedgerKinds.Turn && e.TaskId == task.Id.Value) turns.Add(e);

        var outcome = stop switch
        {
            null => "passed",
            { Status: TeamStatus.Blocked } => "blocked",
            _ => "failed",
        };
        await svc.LogAsync(runId, "rerun", LedgerKinds.Run, outcome: outcome, taskId: task.Id.Value, ct: ct);
        return new RerunAttempt(n, runId, outcome, turns.Count, turns.Sum(t => t.TokensIn ?? 0), turns.Sum(t => t.TokensOut ?? 0), stop?.Failure);
    }
}
