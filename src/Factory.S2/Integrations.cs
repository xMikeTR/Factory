using Factory.Harness;

namespace Factory.S2;

public interface IPullRequests
{
    /// Idempotent: returns the existing PR for `head` if there is one.
    Task<string> EnsurePullRequestAsync(string head, string baseBranch, string title, string body, CancellationToken ct);
    Task EnableAutoMergeAsync(string url, CancellationToken ct);
}

/// GitHub via the gh CLI (authenticated as the factory's GitHub App or a bot user).
/// Required approvals per tier are enforced by branch protection / CODEOWNERS on GitHub;
/// the factory only decides whether to enable auto-merge and whom to ask.
public sealed class GhCliPullRequests(string repoPath) : IPullRequests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public async Task<string> EnsurePullRequestAsync(string head, string baseBranch, string title, string body, CancellationToken ct)
    {
        var existing = await Gh(ct, "pr", "list", "--head", head, "--state", "open", "--json", "url", "--jq", ".[0].url");
        if (!string.IsNullOrWhiteSpace(existing)) return existing;

        var bodyFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(bodyFile, body, ct);
        try { return await Gh(ct, "pr", "create", "--head", head, "--base", baseBranch, "--title", title, "--body-file", bodyFile); }
        finally { File.Delete(bodyFile); }
    }

    public Task EnableAutoMergeAsync(string url, CancellationToken ct) => Gh(ct, "pr", "merge", url, "--auto", "--squash");

    private async Task<string> Gh(CancellationToken ct, params string[] args)
    {
        var r = await ProcessRunner.RunAsync("gh", args, repoPath, Timeout, ct: ct);
        if (!r.Succeeded) throw new InvalidOperationException($"gh {string.Join(' ', args)} failed:\n{r.Tail()}");
        return r.Output.Trim();
    }
}

/// Local runs: writes the PR body to the run directory instead of calling GitHub.
public sealed class DryRunPullRequests(string runDirRoot) : IPullRequests
{
    public async Task<string> EnsurePullRequestAsync(string head, string baseBranch, string title, string body, CancellationToken ct)
    {
        var path = Path.Combine(runDirRoot, head.Replace('/', '_') + ".pr.md");
        Directory.CreateDirectory(runDirRoot);
        await File.WriteAllTextAsync(path, $"# {title}\n\n{head} -> {baseBranch}\n\n{body}", ct);
        return new Uri(path).AbsoluteUri;
    }

    public Task EnableAutoMergeAsync(string url, CancellationToken ct) => Task.CompletedTask;
}

/// Where RequestPort questions go. Console for the Phase 2 slice. In production, needs-decision
/// becomes a minted item in the registry that System 1's grill-me picks up (the run waits,
/// checkpointed, until the decision is bound), and merge approval resolves from a GitHub webhook.
public interface IHumanChannel
{
    Task<NeedsDecisionResponse> AskAsync(NeedsDecisionRequest request, CancellationToken ct);
    Task<MergeApprovalResponse> ApproveAsync(MergeApprovalRequest request, CancellationToken ct);
}

public sealed class ConsoleHumanChannel : IHumanChannel
{
    public Task<NeedsDecisionResponse> AskAsync(NeedsDecisionRequest request, CancellationToken ct)
    {
        var answers = new List<DecisionAnswer>();
        foreach (var b in request.Blocked)
        {
            Console.WriteLine($"\n[needs-decision] {b.TaskId} ({b.TeamId}, raised by {b.Source})\n  {b.Question}");
            Console.Write("  Bind a decision id and note, e.g. 'D-0043 retry at most 3 times', or 'abandon <reason>': ");
            var line = (Console.ReadLine() ?? "").Trim();
            if (line.StartsWith("abandon", StringComparison.OrdinalIgnoreCase) || line.Length == 0)
                answers.Add(new DecisionAnswer(b.TaskId, null, line.Length > 7 ? line[7..].Trim() : "no answer"));
            else
            {
                var parts = line.Split(' ', 2);
                answers.Add(new DecisionAnswer(b.TaskId, parts[0], parts.Length > 1 ? parts[1] : null));
            }
        }
        return Task.FromResult(new NeedsDecisionResponse(request.RunId, request.WaveIndex, answers.ToArray()));
    }

    public Task<MergeApprovalResponse> ApproveAsync(MergeApprovalRequest request, CancellationToken ct)
    {
        Console.WriteLine($"\n[merge-approval] {request.Tier}: {request.PullRequestUrl}");
        foreach (var r in request.Reasons) Console.WriteLine($"  - {r}");
        Console.Write("  Type 'merged <your name>' once approved and merged, anything else to stop: ");
        var line = (Console.ReadLine() ?? "").Trim();
        var merged = line.StartsWith("merged", StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(new MergeApprovalResponse(request.RunId, merged, merged && line.Length > 6 ? line[6..].Trim() : null));
    }
}
