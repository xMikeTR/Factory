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
    public static async Task<int> RecordAsync(string runDir, string runId, Func<Task<RunOutcome>> body)
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
            throw;
        }
        Write(runDir, runId, outcome.Status, outcome.Reason, outcome.ExitCode);
        return outcome.ExitCode;
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
