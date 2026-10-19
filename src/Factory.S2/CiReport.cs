using System.Text.Json;
using Factory.Core;
using Factory.Harness;

namespace Factory.S2;

/// `factory-s2 ci-report`: CI ran a suite after the run ended and it failed. The failure is written to the run's ledger
/// against the tasks that own the failing files, so `run --repair <run>` re-runs only those tasks with the log tail as
/// their brief. Heavy suites stay out of the run; the cost of finding a failure there is one small repair run.
public static class CiReport
{
    /// The log keeps its end: the error is at the bottom, the noise above it.
    public const int LogTail = 2000;

    /// Null when recorded, else why nothing was recorded. Ownership is the plan's write paths; nothing is guessed.
    public static async Task<string?> RecordAsync(FactoryServices svc, string runId, string check, string log, IReadOnlyList<string> files,
        string? task, string? commit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(check)) return "--check needs the name of the CI check that failed";
        var tasks = svc.LoadDispatch(runId).Waves.SelectMany(w => w.Teams).SelectMany(t => t.Tasks).ToList();

        List<string> blamed;
        if (task is not null)
        {
            if (!tasks.Any(t => t.Id.Value == task)) return $"run {runId} has no task {task} ({string.Join(", ", tasks.Select(t => t.Id.Value))})";
            blamed = [task];
        }
        else
        {
            blamed = tasks.Where(t => files.Any(f => t.Writes.Concat(t.TestWrites).Any(p => Core.Core.Matches(p, f)))).Select(t => t.Id.Value).ToList();
            if (blamed.Count == 0)
                return $"no task of run {runId} writes {(files.Count == 0 ? "any file you named (none given; use --file or --task)" : string.Join(", ", files))}: name the task with --task";
        }

        var tail = log.Length > LogTail ? log[^LogTail..] : log;
        foreach (var id in blamed)
            await svc.Ledger.AppendAsync(new LedgerEvent(runId, "S2", "ci", LedgerKinds.CiFailed, TaskId: id, Outcome: "failed",
                Payload: JsonSerializer.Serialize(new { check, log = tail, files, commit })), ct);
        return null;
    }
}
