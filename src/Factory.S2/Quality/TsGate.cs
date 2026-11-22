using Factory.Core;
using Factory.Harness.Sandbox;
using Factory.S2.Git;
using FsCore = Factory.Core.Core;

namespace Factory.S2.Quality;

/// The repo's forbidden-dependency rules (`quality.forbid`, path globs) over TypeScript/JavaScript imports, ratcheted against the
/// integration branch: an import the baseline already had under the same rule is debt. Runs only when the task touched such a file.
public static class TsGate
{
    private static readonly string[] Extensions = [".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs"];

    public static IReadOnlyList<RuleViolation> Judge(IReadOnlyList<ForbiddenDependency> rules, IEnumerable<CodeEdge> baseline, IEnumerable<CodeEdge> current)
    {
        if (rules.Count == 0) return [];
        var judge = (IEnumerable<CodeEdge> edges) => Fitness.forbidden(FsCore.ToList(rules), FsCore.ToList(edges));
        return Fitness.newRuleViolationsSince(judge(baseline), judge(current)).ToList();
    }

    public static async Task<IReadOnlyList<RuleViolation>> CheckAsync(
        EdgeReads reads, GitWorkspace git, string worktree, string runId, IReadOnlyList<ForbiddenDependency> rules, CancellationToken ct)
    {
        if (rules.Count == 0) return [];
        if (!await TouchedTsAsync(git, worktree, runId, ct)) return [];
        var current = await reads.TsCurrent();
        if (current.Count == 0) return [];
        return Judge(rules, await reads.TsBaseline(), current);
    }

    /// The design's allow list over TS/JS imports (repo-relative files, so a code area matches directly), ratcheted by element pair.
    public static async Task<AllowJudgement> AllowedAsync(
        EdgeReads reads, GitWorkspace git, string worktree, string runId, Handover handover, string? repo, CancellationToken ct)
    {
        if (!await TouchedTsAsync(git, worktree, runId, ct)) return AllowJudgement.Empty;
        var current = await reads.TsCurrent();
        if (current.Count == 0) return AllowJudgement.Empty;
        return JudgeAllowed(handover, repo, await reads.TsBaseline(), current);
    }

    public static AllowJudgement JudgeAllowed(Handover handover, string? repo, IEnumerable<CodeEdge> baseline, IEnumerable<CodeEdge> current)
    {
        var (before, now) = QualityGate.Judge(handover, QualityGate.FsOption(repo), EdgeKind.Import, baseline, current);
        return AllowJudgement.Of(before, now);
    }

    private static async Task<bool> TouchedTsAsync(GitWorkspace git, string worktree, string runId, CancellationToken ct) =>
        (await git.TaskPathsAsync(worktree, runId, ct)).Any(p => Extensions.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase)));

    public static async Task<IReadOnlyList<CodeEdge>> EdgesAsync(ISandboxFactory sandboxes, string team, string path, CancellationToken ct)
    {
        await using var sandbox = await sandboxes.ForTeamAsync(team, path, ct);
        return await TsImportEdges.ReadAsync(sandbox, path, ct);
    }
}
