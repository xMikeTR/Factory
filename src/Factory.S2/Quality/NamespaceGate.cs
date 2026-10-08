using Factory.Core;
using Factory.Harness;
using Factory.Harness.Sandbox;
using Factory.S2.Git;
using FsCore = Factory.Core.Core;

namespace Factory.S2.Quality;

/// Hand-written namespace rules (`quality.forbidNamespaces`) judged on built assemblies, ratcheted against a baseline build:
/// an edge the baseline already had under the same rule is debt, not the task's doing.
public static class NamespaceGate
{
    public static IReadOnlyList<RuleViolation> Judge(IReadOnlyList<ForbiddenDependency> rules, IEnumerable<string> baseline, IEnumerable<string> current)
    {
        if (rules.Count == 0) return [];
        var judge = (IEnumerable<string> dlls) => Fitness.forbidden(FsCore.ToList(rules), FsCore.ToList(NamespaceEdges.Read(dlls)));
        return Fitness.newRuleViolationsSince(judge(baseline), judge(current)).ToList();
    }

    /// Builds the integrated worktree and a baseline checkout in the sandbox, then judges the assemblies. A build that fails
    /// cannot be judged (the compile check reports it), so it yields no assemblies rather than a verdict.
    public static async Task<IReadOnlyList<RuleViolation>> CheckAsync(
        ISandboxFactory sandboxes, GitWorkspace git, string worktree, string runId, IReadOnlyList<ForbiddenDependency> rules, CancellationToken ct)
    {
        if (rules.Count == 0) return [];
        var current = await BuildAsync(sandboxes, "arch-current", worktree, ct);
        if (current.Count == 0) return [];
        await using var baseline = await git.BaselineCheckoutAsync(runId, ct);
        return Judge(rules, await BuildAsync(sandboxes, "arch-baseline", baseline.Path, ct), current);
    }

    public static async Task<IReadOnlyList<string>> BuildAsync(ISandboxFactory sandboxes, string team, string path, CancellationToken ct)
    {
        await using var sandbox = await sandboxes.ForTeamAsync(team, path, ct);
        var build = await sandbox.RunAsync("dotnet build --nologo -v q", path, TimeSpan.FromMinutes(10), new Dictionary<string, string>(), ct);
        return build.ExitCode == 0 && !build.TimedOut ? BuiltAssemblies(path) : [];
    }

    /// The newest bin/**/{project name}.dll of every project on disk (an assembly's name defaults to its project's).
    public static IReadOnlyList<string> BuiltAssemblies(string path) =>
        Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".csproj") || f.EndsWith(".fsproj")) && !f.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}"))
            .Select(proj => Directory.EnumerateFiles(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(proj)!, "bin"), System.IO.Path.GetFileNameWithoutExtension(proj) + ".dll", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault())
            .OfType<string>()
            .ToList();
}
