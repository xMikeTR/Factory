using Factory.S2.Git;

namespace Factory.S2;

/// The merge step of a split run (`--repo Api=.. --repo Ui=..`): one PR per repo, each from that repo's
/// integration branch. A person merges them; the factory never does. The run is complete when all are merged (`close`).
public static class SplitMerge
{
    public sealed record RepoPr(string Repo, string Url);

    /// `Api` first (the contract owner merges first), the rest in the order named.
    public static IReadOnlyList<string> Order(IEnumerable<string> repos) => [.. repos.OrderBy(r => r == "Api" ? 0 : 1)];

    /// Opens (or finds) the PR of every repo, in merge order. `host` is the PR host bound to that repo.
    public static async Task<IReadOnlyList<RepoPr>> OpenAsync(IEnumerable<string> repos, string runId, string baseBranch,
        Func<string, IPullRequests> host, Func<string, string> title, Func<string, string> body, CancellationToken ct,
        Func<string, Task>? push = null)
    {
        var branch = GitWorkspace.IntegrationBranch(runId);
        var prs = new List<RepoPr>();
        foreach (var repo in Order(repos))
        {
            if (push is not null) await push(repo);
            prs.Add(new RepoPr(repo, await host(repo).EnsurePullRequestAsync(branch, baseBranch, title(repo), body(repo), ct)));
        }
        return prs;
    }

    public static string MergeOrderNote(IReadOnlyList<string> order, string runId) =>
        $"### Merge order\nThis run spans {string.Join(" and ", order)}. Merge the {order[0]} PR first, then the {string.Join(", then the ", order.Skip(1).Select(o => $"{o} PR"))}. " +
        $"The run is complete only when both are merged: then run `factory-s2 close --run-id {runId}`.\n";

    public static RunFinished Finished(string runId, IReadOnlyList<RepoPr> prs) =>
        new(runId, "ready_for_review", string.Join("\n", prs.Select(p => $"{p.Repo}: {p.Url}")));
}
