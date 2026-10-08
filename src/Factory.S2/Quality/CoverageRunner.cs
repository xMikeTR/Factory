using Factory.Harness.Sandbox;

namespace Factory.S2.Quality;

/// Line coverage for CRAP: each test project is built and run under coverlet.console (installed in the runner image),
/// and its Cobertura report is returned. A project that does not build or run gives no report: unmeasured is not judged.
public static class CoverageRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(15);

    public static async Task<IReadOnlyList<string>> CollectAsync(ICommandSandbox sandbox, string worktree, CancellationToken ct)
    {
        var reports = new List<string>();
        foreach (var project in TestProjects(worktree))
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            var bin = $"/tmp/cov-{id}/bin";
            var out_ = $"/tmp/cov-{id}/out";
            var name = Path.GetFileNameWithoutExtension(project);
            var run = await sandbox.RunAsync(
                $"dotnet build '{project}' --no-restore -v q -o {bin} && coverlet {bin}/{name}.dll --target dotnet --targetargs \"vstest {bin}/{name}.dll\" --format cobertura -o {out_}/",
                worktree, Timeout, new Dictionary<string, string>(), ct);
            if (run.TimedOut) continue;
            var report = await sandbox.RunAsync($"cat {out_}/coverage.cobertura.xml", worktree, TimeSpan.FromMinutes(1), new Dictionary<string, string>(), ct);
            if (report.Succeeded && report.Output.TrimStart().StartsWith('<')) reports.Add(report.Output);
        }
        return reports;
    }

    private static readonly System.Text.RegularExpressions.Regex Safe = new(@"^[A-Za-z0-9_./-]+$");

    private static IEnumerable<string> TestProjects(string worktree) =>
        Directory.EnumerateFiles(worktree, "*.*proj", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".csproj") || p.EndsWith(".fsproj"))
            .Select(p => Path.GetRelativePath(worktree, p).Replace('\\', '/'))
            .Where(p => !p.Split('/').Any(seg => seg is "bin" or "obj" or "node_modules") && Safe.IsMatch(p))
            .Where(p => File.ReadAllText(Path.Combine(worktree, p)).Contains("Microsoft.NET.Test.Sdk", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);
}
