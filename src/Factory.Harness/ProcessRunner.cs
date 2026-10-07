using System.Diagnostics;
using System.Text;

namespace Factory.Harness;

public sealed record ProcessResult(int ExitCode, string Output, TimeSpan Duration, bool TimedOut)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;

    /// Last `max` characters: enough for a failing assertion, small enough for a prompt or ledger row.
    public string Tail(int max = 4000) => Output.Length <= max ? Output : "…" + Output[^max..];
}

/// Runs shell commands (verify blocks, tests, git, gh). One place for timeouts and process-tree kill.
public static class ProcessRunner
{
    /// Shell used for verify and test commands. On Windows dev machines point this at Git Bash.
    public static string Shell { get; set; } = Environment.GetEnvironmentVariable("FACTORY_SHELL") ?? "bash";

    public static Task<ProcessResult> ShellAsync(
        string command, string workDir, TimeSpan timeout,
        IReadOnlyDictionary<string, string>? env = null, CancellationToken ct = default, bool inheritEnvironment = true) =>
        RunAsync(Shell, ["-c", command], workDir, timeout, env, ct, inheritEnvironment);

    public static async Task<ProcessResult> RunAsync(
        string file, IEnumerable<string> args, string workDir, TimeSpan timeout,
        IReadOnlyDictionary<string, string>? env = null, CancellationToken ct = default, bool inheritEnvironment = true)
    {
        var psi = new ProcessStartInfo(file)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (!inheritEnvironment) psi.Environment.Clear();
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;

        var output = new StringBuilder();
        var sw = Stopwatch.StartNew();
        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            await proc.WaitForExitAsync(CancellationToken.None);
            if (!timedOut) throw;
        }

        string text;
        lock (output) text = output.ToString();
        return new ProcessResult(timedOut ? 124 : proc.ExitCode, text, sw.Elapsed, timedOut);
    }
}
