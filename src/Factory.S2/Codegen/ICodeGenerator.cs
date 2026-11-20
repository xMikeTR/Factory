using Factory.Core;
using Factory.Harness.Sandbox;

namespace Factory.S2;

/// Outcome is `generated`, `failed` (a command exited non-zero) or `could_not_run` (no sandbox, or the tool is not the pinned version).
public sealed record CodegenRun(string Outcome, string Detail, IReadOnlyList<string> Commands);

/// Runs the `Codegen.commands` of each target in `workDir`: inputs under `in/`, outputs under `out/<repo>/`.
public interface ICodeGenerator
{
    Task<CodegenRun> GenerateAsync(IReadOnlyList<Codegen.Target> targets, string workDir, CancellationToken ct);
}

/// NSwag in the runner image (pinned there; `nswag version` must say so), offline.
public sealed class SandboxCodeGenerator(ISandboxFactory sandboxes) : ICodeGenerator
{
    private static readonly TimeSpan CommandLimit = TimeSpan.FromMinutes(2);
    private static readonly Dictionary<string, string> NoEnv = [];

    public async Task<CodegenRun> GenerateAsync(IReadOnlyList<Codegen.Target> targets, string workDir, CancellationToken ct)
    {
        var commands = targets.SelectMany(t => Codegen.commands(t)).ToList();
        string Missing(string why) =>
            $"{why}: NSwag {Codegen.nswagVersion} is expected in the runner image; rebuild the runner image (docker/runner/build.sh)";
        foreach (var t in targets)
            foreach (var f in t.Files) Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(workDir, Codegen.outPath(t.Repo, f)))!);

        ICommandSandbox sandbox;
        try { sandbox = await sandboxes.ForTeamAsync("codegen", workDir, ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new CodegenRun("could_not_run", Missing("no sandbox (" + e.Message + ")"), commands);
        }
        await using (sandbox)
        {
            var version = await sandbox.RunAsync("nswag version", workDir, TimeSpan.FromMinutes(1), NoEnv, ct);
            if (!version.Succeeded || !version.Output.Contains(Codegen.nswagVersion, StringComparison.Ordinal))
                return new CodegenRun("could_not_run", Missing("`nswag version` did not report " + Codegen.nswagVersion + " (" + version.Tail(300).Trim() + ")"), commands);
            foreach (var command in commands)
            {
                var run = await sandbox.RunAsync(command, workDir, CommandLimit, NoEnv, ct);
                if (!run.Succeeded) return new CodegenRun("failed", $"{command}\nexit {run.ExitCode}{(run.TimedOut ? " (timed out)" : "")}\n{run.Tail(2000)}", commands);
            }
        }
        return new CodegenRun("generated", "", commands);
    }
}
