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
        ISandboxFactory sandboxes, GitWorkspace git, string worktree, string runId, IReadOnlyList<ForbiddenDependency> rules, CancellationToken ct)
    {
        if (rules.Count == 0) return [];
        if (!(await git.TaskPathsAsync(worktree, runId, ct)).Any(p => Extensions.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase)))) return [];
        var current = await EdgesAsync(sandboxes, "ts-current", worktree, ct);
        if (current.Count == 0) return [];
        await using var baseline = await git.BaselineCheckoutAsync(runId, ct);
        return Judge(rules, await EdgesAsync(sandboxes, "ts-baseline", baseline.Path, ct), current);
    }

    private static async Task<IReadOnlyList<CodeEdge>> EdgesAsync(ISandboxFactory sandboxes, string team, string path, CancellationToken ct)
    {
        await using var sandbox = await sandboxes.ForTeamAsync(team, path, ct);
        return await TsImportEdges.ReadAsync(sandbox, path, ct);
    }
}
