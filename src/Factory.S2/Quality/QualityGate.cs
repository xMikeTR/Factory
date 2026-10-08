using Factory.Core;
using Factory.S2.Git;

namespace Factory.S2.Quality;

using Core = Factory.Core.Core;
using CoreQuality = Factory.Core.Quality;

/// The ratchet for one task: what the task's diff did to the functions it touched, against the
/// integration branch as the baseline. C# only so far (other languages join as adapters are added).
public static class QualityGate
{
    public static async Task<IReadOnlyList<QualityViolation>> EvaluateAsync(
        GitWorkspace git, string worktree, string runId, QualityThresholds thresholds, CancellationToken ct)
    {
        var added = Diff.parse(await git.TaskDiffAsync(worktree, runId, ct)).Added
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
        return CoreQuality.check(thresholds, Core.ToList(baseline), Core.ToList(current), Core.ToList(changed)).ToList();
    }

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
