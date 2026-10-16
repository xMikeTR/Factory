using System.Text.Json;
using Factory.Core;

namespace Factory.S2;

/// How a run ended: a RunFinished status (merged, parked, ...), or refused / no_result from the CLI.
public sealed record RunOutcome(string Status, string Reason, int ExitCode);

/// `.factory/runs/<id>/result.json`: how the last attempt at a run ended and why, written on every exit
/// path (a crash is `failed`, a rejected input `rejected`, Ctrl+C `cancelled`) so the reason is on disk,
/// not only in the terminal. A resume replaces it.
public static class RunOutcomeFile
{
    public static string PathIn(string runDir) => Path.Combine(runDir, "result.json");

    /// Runs the body and records its outcome; an exception is recorded, then rethrown unchanged.
    /// `ended` hears every outcome (best effort: its own failure is printed, never thrown).
    public static async Task<int> RecordAsync(string runDir, string runId, Func<Task<RunOutcome>> body, Func<RunOutcome, Task>? ended = null)
    {
        RunOutcome outcome;
        try { outcome = await body(); }
        catch (Exception e)
        {
            var status = e switch
            {
                OperationCanceledException => "cancelled",
                FactoryRejectedException => "rejected",
                _ => "failed",
            };
            var reason = e is FactoryRejectedException or OperationCanceledException ? e.Message : $"{e.GetType().Name}: {e.Message}";
            Write(runDir, runId, status, reason, null);
            await Report(ended, new RunOutcome(status, reason, 1));
            throw;
        }
        Write(runDir, runId, outcome.Status, outcome.Reason, outcome.ExitCode);
        await Report(ended, outcome);
        return outcome.ExitCode;
    }

    private static async Task Report(Func<RunOutcome, Task>? ended, RunOutcome outcome)
    {
        if (ended is null) return;
        try { await ended(outcome); }
        catch (Exception e) { Console.Error.WriteLine($"run end not reported ({e.GetType().Name}: {e.Message})"); }
    }

    private static void Write(string runDir, string runId, string status, string reason, int? exitCode)
    {
        Directory.CreateDirectory(runDir);
        var json = JsonSerializer.Serialize(new { run = runId, status, reason, exitCode, at = DateTimeOffset.UtcNow },
            new JsonSerializerOptions { WriteIndented = true });
        var tmp = PathIn(runDir) + ".tmp";
        File.WriteAllText(tmp, json + "\n");
        File.Move(tmp, PathIn(runDir), overwrite: true);
    }
}
