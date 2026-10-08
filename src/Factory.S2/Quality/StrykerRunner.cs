using Factory.Core;
using Factory.Harness.Sandbox;

namespace Factory.S2.Quality;

/// Stryker.NET in the team's sandbox (installed in the runner image). Mutates only the given files, so the
/// run costs what the task changed. A run that leaves no report did not finish, which says nothing about the tests.
public sealed class StrykerRunner(ISandboxFactory sandboxes, TimeSpan? timeout = null) : IMutationRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(20);
    private static readonly System.Text.RegularExpressions.Regex SafePath = new(@"^[A-Za-z0-9_./ -]+$");

    public async Task<MutationRun> RunAsync(string worktree, IReadOnlyList<string> files, CancellationToken ct)
    {
        foreach (var f in files)
            if (!SafePath.IsMatch(f) || f.Contains("..")) throw new ArgumentException($"not a plain repo-relative path: {f}", nameof(files));

        await using var sandbox = await sandboxes.ForTeamAsync("mutation", worktree, ct);
        var solution = Directory.EnumerateFiles(worktree, "*.sln*", SearchOption.TopDirectoryOnly)
            .Where(p => p.EndsWith(".sln") || p.EndsWith(".slnx")).Select(Path.GetFileName).Order().FirstOrDefault();
        var output = $"/tmp/stryker-{Guid.NewGuid():N}";
        var mutate = string.Join(' ', files.Select(f => $"--mutate '**/{f}'"));
        var command = $"dotnet-stryker {(solution is null ? "" : $"--solution {solution} ")}{mutate} --reporter json --output {output} --disable-bail";
        var run = await sandbox.RunAsync(command, worktree, timeout ?? DefaultTimeout, new Dictionary<string, string>(), ct);
        if (run.TimedOut) return new MutationRun([], true);

        var report = await sandbox.RunAsync($"cat $(ls -d {output}/reports/mutation-report.json)", worktree, TimeSpan.FromMinutes(1), new Dictionary<string, string>(), ct);
        return report.Succeeded && report.Output.TrimStart().StartsWith('{')
            ? new MutationRun(StrykerReport.Parse(report.Output, worktree), false)
            : new MutationRun([], true);
    }
}
