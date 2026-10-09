using Factory.Core;
using Factory.Harness.Sandbox;
using Factory.S2.Git;
using Microsoft.FSharp.Collections;

namespace Factory.S2.Quality;

using Core = Factory.Core.Core;
using CoreQuality = Factory.Core.Quality;

/// The ratchet for one task: what the task's diff did to the functions it touched, against the
/// integration branch as the baseline. C# (Roslyn), F# (FSharp.Compiler.Service) and TypeScript (ESLint in the sandbox); other languages join as adapters are added.
public static class QualityGate
{
    public static async Task<IReadOnlyList<QualityViolation>> EvaluateAsync(
        GitWorkspace git, string worktree, string runId, QualityThresholds thresholds, CancellationToken ct, ISandboxFactory? sandboxes = null, bool coverage = false)
    {
        var all = Diff.parse(await git.TaskDiffAsync(worktree, runId, ct)).Added;
        var added = all
            .Where(kv => IsMeasured(kv.Key) && File.Exists(Path.Combine(worktree, kv.Key)))
            .ToList();

        var baseline = new List<FunctionMetrics>();
        var current = new List<FunctionMetrics>();
        var changed = new List<ChangedRange>();
        foreach (var (file, lines) in added.Select(kv => (kv.Key, kv.Value)))
        {
            current.AddRange(Measure(file, await File.ReadAllTextAsync(Path.Combine(worktree, file), ct)));
            if (await git.BaseFileAsync(worktree, runId, file, ct) is { } before) baseline.AddRange(Measure(file, before));
            changed.AddRange(Ranges(file, lines));
        }
        // TypeScript: ESLint runs in the sandbox, current and baseline source under the same file name.
        var typescript = all.Where(kv => IsTypeScript(kv.Key) && File.Exists(Path.Combine(worktree, kv.Key))).ToList();
        var withCoverage = coverage && added.Count > 0;
        if (sandboxes is not null && (typescript.Count > 0 || withCoverage))
        {
            await using var sandbox = await sandboxes.ForTeamAsync("quality", worktree, ct);
            // Coverage of the current code only: a baseline without it is judged on cyclomatic and length, never CRAP.
            if (withCoverage) current = Cobertura.Join(current, await CoverageRunner.CollectAsync(sandbox, worktree, ct)).ToList();
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
        var (before, current) = await FitnessJudgedAsync(git, worktree, runId, handover, ct);
        return Fitness.newSince(before, current).ToList();
    }

    /// The debt the ratchet lets through: element pairs the touched projects already crossed on the baseline and still cross.
    public static async Task<IReadOnlyList<(ElementId From, ElementId To)>> FitnessDebtAsync(
        GitWorkspace git, string worktree, string runId, Handover handover, CancellationToken ct)
    {
        var (before, current) = await FitnessJudgedAsync(git, worktree, runId, handover, ct);
        return Fitness.debt(before, current).Select(p => (p.Item1, p.Item2)).ToList();
    }

    /// The model's verdict on the task-touched projects' references, as on the baseline and as in the worktree.
    private static async Task<(FSharpList<FitnessViolation> Before, FSharpList<FitnessViolation> Current)> FitnessJudgedAsync(
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
        return (judge(before), judge(current));
    }

    /// Project references the diff added or changed that a hand-written rule in the policy forbids, minus those the baseline already had.
    public static async Task<IReadOnlyList<RuleViolation>> ForbiddenAsync(
        GitWorkspace git, string worktree, string runId, IReadOnlyList<ForbiddenDependency> rules, CancellationToken ct)
    {
        if (rules.Count == 0) return [];
        var current = new List<CodeEdge>();
        var before = new List<CodeEdge>();
        foreach (var p in (await git.TaskPathsAsync(worktree, runId, ct))
            .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(worktree, p))))
        {
            current.AddRange(CsprojEdges.Read(p, await File.ReadAllTextAsync(Path.Combine(worktree, p), ct)));
            if (await git.BaseFileAsync(worktree, runId, p, ct) is { } old) before.AddRange(CsprojEdges.Read(p, old));
        }
        var judge = (IEnumerable<CodeEdge> edges) => Fitness.forbidden(Core.ToList(rules), Core.ToList(edges));
        return Fitness.newRuleViolationsSince(judge(before), judge(current)).ToList();
    }

    /// C# (Roslyn) and F# (FSharp.Compiler.Service) are measured in-process; TypeScript goes through ESLint in the sandbox.
    private static bool IsMeasured(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".fs", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<FunctionMetrics> Measure(string file, string source) =>
        file.EndsWith(".fs", StringComparison.OrdinalIgnoreCase)
            ? Factory.Quality.FSharp.FSharpMetrics.measure(file, source).ToList()
            : CSharpMetrics.Measure(file, source);

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
