using System.Diagnostics;

namespace Factory.Harness.Registry;

/// One parity check of a work item: every registry scenario replayed on the new app, labelled with the commit it serves.
/// Adapter is designc's (`web`, or `fixture:<file>` for a dry run); null = designc.json's.
public sealed record ParityCheck(string Registry, string Commit, string Item, string? BaseUrl, string? Adapter);

/// What designc's recorder said. Pass, Fail and CouldNotRun are already parity_result rows in the ledger; Error is an
/// infrastructure problem (no baseline, bad scenario, the adapter could not open) and nothing was judged.
public enum ParityRecording { Pass, Fail, CouldNotRun, Error }

/// Runs designc's `scripts/parity-record.js` (docs/parity-contract.md), which owns the comparison and appends the
/// parity_result rows itself. Always the next side: S2 never records legacy, and never allows writes or extra requests,
/// since the arguments that would are not built.
public static class ParityRecorder
{
    public static IReadOnlyList<string> Arguments(string script, ParityCheck c)
    {
        var args = new List<string> { script, "--registry", c.Registry, "--scenario", "all", "--side", "next",
            "--commit", c.Commit, "--item", c.Item, "--ledger", "--by", RegistryLedgerProjector.By };
        if (c.BaseUrl is not null) args.AddRange(["--base-url", c.BaseUrl]);
        if (c.Adapter is not null) args.AddRange(["--adapter", c.Adapter]);
        return args;
    }

    /// designc's exit codes; with several scenarios the worst wins (2 > 1 > 3 > 0).
    public static ParityRecording Interpret(int exitCode) => exitCode switch
    {
        0 => ParityRecording.Pass,
        1 => ParityRecording.Fail,
        3 => ParityRecording.CouldNotRun,
        _ => ParityRecording.Error,
    };

    public static async Task<(ParityRecording Result, string Output)> RecordAsync(string designcDir, ParityCheck c, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("node") { WorkingDirectory = designcDir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in Arguments(Path.Combine(designcDir, "scripts", "parity-record.js"), c)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("node did not start");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (Interpret(p.ExitCode), (await stdout) + (await stderr));
    }
}
