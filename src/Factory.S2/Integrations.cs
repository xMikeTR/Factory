using System.Text.Json;
using Factory.Core;
using Factory.Harness;

namespace Factory.S2;

public interface IPullRequests
{
    /// Idempotent: returns the existing PR for `head` if there is one.
    Task<string> EnsurePullRequestAsync(string head, string baseBranch, string title, string body, CancellationToken ct);
    Task EnableAutoMergeAsync(string url, CancellationToken ct);
}

/// A PR host whose comments carry approvals (`/approve <reason>`, `/verified <check>`). Tiered
/// mode reads them on every run: too few parks the run, enough merges it. Without this the
/// merge-approval port asks a human instead (dry-run).
public interface IApprovalComments
{
    Task<IReadOnlyList<PrComment>> CommentsAsync(string url, CancellationToken ct);
    Task MergeAsync(string url, CancellationToken ct);
}

/// GitHub via the gh CLI, authenticated as the operator. Who may approve is the policy's
/// allow-list (Merge.approvals), never "whoever commented".
public sealed class GhCliPullRequests(string repoPath) : IPullRequests, IApprovalComments
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

    public Task MergeAsync(string url, CancellationToken ct) => Gh(ct, "pr", "merge", url, "--squash");

    public async Task<IReadOnlyList<PrComment>> CommentsAsync(string url, CancellationToken ct)
    {
        return ParseComments(await Gh(ct, "pr", "view", url, "--json", "comments"));
    }

    /// A comment with no author (a deleted account) or no body is kept with empty text, so it can never count as an approval.
    public static IReadOnlyList<PrComment> ParseComments(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("comments", out var comments) || comments.ValueKind != JsonValueKind.Array) return [];
        static string Text(JsonElement parent, string name) =>
            parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        return comments.EnumerateArray()
            .Select(c => new PrComment(
                c.ValueKind == JsonValueKind.Object && c.TryGetProperty("author", out var a) ? Text(a, "login") : "",
                Text(c, "body")))
            .ToList();
    }

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

/// Where merge-approval requests go. Console for the Phase 2 slice; a GitHub webhook in production.
/// Needs-decisions do not come here: they park the run in the registry (see WaveExecutor).
public interface IHumanChannel
{
    Task<MergeApprovalResponse> ApproveAsync(MergeApprovalRequest request, CancellationToken ct);
}

public sealed class ConsoleHumanChannel : IHumanChannel
{
    public Task<MergeApprovalResponse> ApproveAsync(MergeApprovalRequest request, CancellationToken ct)
    {
        Console.WriteLine($"\n[merge-approval] {request.Tier}: {request.PullRequestUrl}");
        foreach (var r in request.Reasons) Console.WriteLine($"  - {r}");
        Console.Write("  Type 'merged <your name>' once approved and merged, anything else to stop: ");
        return Task.FromResult(Answer(request.RunId, Console.ReadLine() ?? ""));
    }

    /// `merged` alone, or `merged <name>`. A word that only starts with it ("mergedd", "merged-not-yet") stops the run.
    public static MergeApprovalResponse Answer(string runId, string typed)
    {
        var line = typed.Trim();
        var merged = line.Equals("merged", StringComparison.OrdinalIgnoreCase)
            || (line.StartsWith("merged", StringComparison.OrdinalIgnoreCase) && char.IsWhiteSpace(line[6]));
        return new MergeApprovalResponse(runId, merged, merged && line.Length > 6 ? line[6..].Trim() : null);
    }
}
