using Factory.Core;
using Factory.Harness.Sandbox;

namespace Factory.S2.Quality;

/// Stryker.NET in the team's sandbox (installed in the runner image). Mutates only the given files, so the
/// run costs what the task changed. A run that leaves no report did not finish, which says nothing about the tests.
public sealed class StrykerRunner(ISandboxFactory sandboxes, TimeSpan? timeout = null, TimeSpan? fullTimeout = null) : IMutationRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(20);
    private static readonly System.Text.RegularExpressions.Regex SafePath = new(@"^[A-Za-z0-9_./ -]+$");

    public Task<MutationRun> RunAsync(string worktree, IReadOnlyList<string> files, CancellationToken ct)
    {
        foreach (var f in files)
            if (!SafePath.IsMatch(f) || f.Contains("..")) throw new ArgumentException($"not a plain repo-relative path: {f}", nameof(files));
        // A glob that matches no file makes Stryker mutate the whole project, so it is never passed on.
        var missing = files.Where(f => !File.Exists(Path.Combine(worktree, f))).ToList();
        var skipped = missing.Count == 0 ? null
            : $"--mutate glob{(missing.Count > 1 ? "s" : "")} {string.Join(", ", missing.Select(f => $"'**/{f}'"))} match{(missing.Count > 1 ? "" : "es")} no file, so no mutant run for {(missing.Count > 1 ? "them" : "it")}";
        var present = files.Except(missing).ToList();
        if (present.Count == 0) return Task.FromResult(new MutationRun([], false, skipped));
        return RunCoreAsync(worktree, present, timeout ?? DefaultTimeout, ct, skipped);
    }

    /// No `--mutate`: every file Stryker finds. Its own, longer timeout (FACTORY_MUTATION_FULL_TIMEOUT_MIN).
    public Task<MutationRun> RunAllAsync(string worktree, CancellationToken ct) =>
        RunCoreAsync(worktree, [], fullTimeout ?? TimeSpan.FromMinutes(120), ct, null);

    private async Task<MutationRun> RunCoreAsync(string worktree, IReadOnlyList<string> files, TimeSpan limit, CancellationToken ct, string? skipped)
    {
        await using var sandbox = await sandboxes.ForTeamAsync("mutation", worktree, ct);
        var solution = Directory.EnumerateFiles(worktree, "*.sln*", SearchOption.TopDirectoryOnly)
            .Where(p => p.EndsWith(".sln") || p.EndsWith(".slnx")).Select(Path.GetFileName).Order().FirstOrDefault();
        var output = $"/tmp/stryker-{Guid.NewGuid():N}";
        var mutate = string.Join(' ', files.Select(f => $"--mutate '**/{f}'"));
        var command = $"dotnet-stryker {(solution is null ? "" : $"--solution {solution} ")}{mutate} --reporter json --output {output} --disable-bail";
        var run = await sandbox.RunAsync(command, worktree, limit, new Dictionary<string, string>(), ct);
        if (run.TimedOut) return new MutationRun([], true, skipped);

        var report = await sandbox.RunAsync($"cat $(ls -d {output}/reports/mutation-report.json)", worktree, TimeSpan.FromMinutes(1), new Dictionary<string, string>(), ct);
        return report.Succeeded && report.Output.TrimStart().StartsWith('{')
            ? new MutationRun(StrykerReport.Parse(report.Output, worktree), false, skipped)
            : new MutationRun([], true, skipped);
    }
}
