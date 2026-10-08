using Factory.Core;
using Factory.Harness.Sandbox;
using Factory.S2.Git;

namespace Factory.S2.Quality;

using Core = Factory.Core.Core;
using CoreQuality = Factory.Core.Quality;

/// The ratchet for one task: what the task's diff did to the functions it touched, against the
/// integration branch as the baseline. C# (Roslyn) and TypeScript (ESLint in the sandbox); other languages join as adapters are added.
public static class QualityGate
{
    public static async Task<IReadOnlyList<QualityViolation>> EvaluateAsync(
        GitWorkspace git, string worktree, string runId, QualityThresholds thresholds, CancellationToken ct, ISandboxFactory? sandboxes = null)
    {
        var all = Diff.parse(await git.TaskDiffAsync(worktree, runId, ct)).Added;
        var added = all
            .Where(kv => kv.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(worktree, kv.Key)))
            .ToList();

        var baseline = new List<FunctionMetrics>();
        var current = new List<FunctionMetrics>();
        var changed = new List<ChangedRange>();
        foreach (var (file, lines) in added.Select(kv => (kv.Key, kv.Value)))
        {
            current.AddRange(CSharpMetrics.Measure(file, await File.ReadAllTextAsync(Path.Combine(worktree, file), ct)));
            if (await git.BaseFileAsync(worktree, runId, file, ct) is { } before) baseline.AddRange(CSharpMetrics.Measure(file, before));
            changed.AddRange(Ranges(file, lines));
        }
        // TypeScript: ESLint runs in the sandbox, current and baseline source under the same file name.
        var typescript = all.Where(kv => IsTypeScript(kv.Key) && File.Exists(Path.Combine(worktree, kv.Key))).ToList();
        if (sandboxes is not null && typescript.Count > 0)
        {
            await using var sandbox = await sandboxes.ForTeamAsync("quality", worktree, ct);
            foreach (var (file, lines) in typescript.Select(kv => (kv.Key, kv.Value)))
            {
                current.AddRange(await EslintRunner.MeasureAsync(sandbox, worktree, file, null, ct));
                if (await git.BaseFileAsync(worktree, runId, file, ct) is { } before)
                    baseline.AddRange(await EslintRunner.MeasureAsync(sandbox, worktree, file, before, ct));
                changed.AddRange(Ranges(file, lines));
            }
        }
        return CoreQuality.check(thresholds, Core.ToList(baseline), Core.ToList(current), Core.ToList(changed)).ToList();
    }

    /// The lines of C# the task's diff added or changed, per file (what mutation judges).
    public static async Task<IReadOnlyList<ChangedRange>> ChangedRangesAsync(GitWorkspace git, string worktree, string runId, CancellationToken ct) =>
        Diff.parse(await git.TaskDiffAsync(worktree, runId, ct)).Added
            .Where(kv => kv.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(worktree, kv.Key)))
            .SelectMany(kv => Ranges(kv.Key, kv.Value)).ToList();

    /// The fitness ratchet for one task: project references the diff added or changed that the model does
    /// not allow, minus the element pairs the baseline already crossed (those are debt, not this task's).
    public static async Task<IReadOnlyList<FitnessViolation>> FitnessAsync(
        GitWorkspace git, string worktree, string runId, Handover handover, CancellationToken ct)
    {
        var projects = (await git.TaskPathsAsync(worktree, runId, ct))
            .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(worktree, p))).ToList();
        var current = new List<CodeEdge>();
        var before = new List<CodeEdge>();
        foreach (var p in projects)
        {
            current.AddRange(CsprojEdges.Read(p, await File.ReadAllTextAsync(Path.Combine(worktree, p), ct)));
            if (await git.BaseFileAsync(worktree, runId, p, ct) is { } old) before.AddRange(CsprojEdges.Read(p, old));
        }
        var judge = (IEnumerable<CodeEdge> edges) => Fitness.violations(handover.Elements, handover.Relationships, Core.ToList(edges));
        return Fitness.newSince(judge(before), judge(current)).ToList();
    }

    private static bool IsTypeScript(string path) =>
        path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<ChangedRange> Ranges(string file, IEnumerable<int> lines)
    {
        int? first = null, last = null;
        foreach (var n in lines.Order())
        {
            if (last is { } l && n == l + 1) { last = n; continue; }
            if (first is { } f) yield return new ChangedRange(file, f, last!.Value);
            first = last = n;
        }
        if (first is { } f2) yield return new ChangedRange(file, f2, last!.Value);
    }
}
