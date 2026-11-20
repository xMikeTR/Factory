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

    /// Judges the forbid rules on the attempt's builds. A build that fails cannot be judged (the compile check reports it), so it
    /// yields no assemblies rather than a verdict.
    public static async Task<IReadOnlyList<RuleViolation>> CheckAsync(EdgeReads reads, IReadOnlyList<ForbiddenDependency> rules)
    {
        if (rules.Count == 0) return [];
        var current = await reads.BuiltCurrent();
        if (current.Count == 0) return [];
        return Judge(rules, (await reads.BuiltBaseline()).Select(b => b.Dll), current.Select(b => b.Dll));
    }

    private static readonly string[] CodeFiles = [".cs", ".fs", ".csproj", ".fsproj"];

    /// The design's allow list over cross-assembly type use, in the task's repo: the project of each type's assembly is the edge end, so
    /// A -> B -> C with A using C's types is caught. Runs only when the task touched .NET code and the repo has two or more elements
    /// (otherwise there is no cross-element edge, and the two builds are not worth it). Element boundaries inside one assembly are out of scope.
    public static async Task<AllowJudgement> AllowedAsync(
        EdgeReads reads, GitWorkspace git, string worktree, string runId, Handover handover, string? repo, CancellationToken ct)
    {
        var inRepo = handover.Elements.Count(e => repo is null || (e.Repo is { } r && r.Value == repo));
        if (inRepo < 2) return AllowJudgement.Empty;
        if (!(await git.TaskPathsAsync(worktree, runId, ct)).Any(p => CodeFiles.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase)))) return AllowJudgement.Empty;
        var current = await reads.BuiltCurrent();
        if (current.Count == 0) return AllowJudgement.Empty;
        return JudgeAllowed(handover, repo, await reads.BuiltBaseline(), current);
    }

    /// Cross-project type use in the baseline and current builds, judged against the allow list and ratcheted by element pair.
    public static AllowJudgement JudgeAllowed(
        Handover handover, string? repo, IEnumerable<(string Dll, string Project)> baseline, IEnumerable<(string Dll, string Project)> current)
    {
        var (before, now) = QualityGate.Judge(handover, QualityGate.FsOption(repo), EdgeKind.AssemblyUse, NamespaceEdges.ReadProjects(baseline.ToList()), NamespaceEdges.ReadProjects(current.ToList()));
        return AllowJudgement.Of(before, now);
    }

    public static async Task<IReadOnlyList<string>> BuildAsync(ISandboxFactory sandboxes, string team, string path, CancellationToken ct) =>
        (await BuildProjectsAsync(sandboxes, team, path, ct)).Select(b => b.Dll).ToList();

    public static async Task<IReadOnlyList<(string Dll, string Project)>> BuildProjectsAsync(ISandboxFactory sandboxes, string team, string path, CancellationToken ct)
    {
        await using var sandbox = await sandboxes.ForTeamAsync(team, path, ct);
        var build = await sandbox.RunAsync("dotnet build --nologo -v q", path, TimeSpan.FromMinutes(10), new Dictionary<string, string>(), ct);
        return build.ExitCode == 0 && !build.TimedOut ? BuiltProjects(path) : [];
    }

    /// The newest bin/**/{project name}.dll of every project on disk (an assembly's name defaults to its project's), with the
    /// repo-relative path of that project (forward slashes).
    public static IReadOnlyList<(string Dll, string Project)> BuiltProjects(string path) =>
        Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".csproj") || f.EndsWith(".fsproj")) && !f.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}"))
            .Where(proj => Directory.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(proj)!, "bin")))
            .Select(proj => (Dll: Directory.EnumerateFiles(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(proj)!, "bin"), System.IO.Path.GetFileNameWithoutExtension(proj) + ".dll", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault(),
                Project: System.IO.Path.GetRelativePath(path, proj).Replace('\\', '/')))
            .Where(b => b.Dll is not null)
            .Select(b => (b.Dll!, b.Project))
            .ToList();
}
