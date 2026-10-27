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

/// A real PR host (not dry-run) that can say a person merged the run's PR: `factory-s2 close` asks it.
public interface IMergedPullRequests
{
    /// The url of the merged PR from `head`, or null when none is merged.
    Task<string?> MergedPullRequestAsync(string head, CancellationToken ct);
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
public sealed class GhCliPullRequests(string repoPath) : IPullRequests, IApprovalComments, IMergedPullRequests
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

    public async Task<string?> MergedPullRequestAsync(string head, CancellationToken ct)
    {
        var url = await Gh(ct, "pr", "list", "--head", head, "--state", "merged", "--json", "url", "--jq", ".[0].url");
        return string.IsNullOrWhiteSpace(url) ? null : url;
    }

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

/// Azure DevOps Repos via the az CLI (azure-devops extension), signed in as the operator. The organisation,
/// project and repository come from the repo's origin remote (az's --detect), so nothing about them is configured.
/// No approval comments: tiered mode on Azure DevOps asks at the console, as dry-run does.
public sealed class AzCliPullRequests(string repoPath, string az = "az") : IPullRequests, IMergedPullRequests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// Azure DevOps refuses a PR description over 4000 characters.
    public const int MaxDescription = 4000;

    /// "<repository web url>/pullrequest/<id>" for the first PR of a list or a created one.
    private const string UrlOf = "join('/pullrequest/', [repository.webUrl, to_string(pullRequestId)])";

    public async Task<string> EnsurePullRequestAsync(string head, string baseBranch, string title, string body, CancellationToken ct)
    {
        var existing = await Az(ct, "repos", "pr", "list", "--source-branch", head, "--status", "active", "--query", $"[0].{UrlOf}", "-o", "tsv");
        if (!string.IsNullOrWhiteSpace(existing)) return existing;
        return await Az(ct, "repos", "pr", "create", "--source-branch", head, "--target-branch", baseBranch,
            "--title", title, "--description", Description(body), "--query", UrlOf, "-o", "tsv");
    }

    public Task EnableAutoMergeAsync(string url, CancellationToken ct) =>
        Az(ct, "repos", "pr", "update", "--id", IdOf(url), "--auto-complete", "true", "--squash", "true");

    public async Task<string?> MergedPullRequestAsync(string head, CancellationToken ct)
    {
        var url = await Az(ct, "repos", "pr", "list", "--source-branch", head, "--status", "completed", "--query", $"[0].{UrlOf}", "-o", "tsv");
        return string.IsNullOrWhiteSpace(url) ? null : url;
    }

    /// The body as is when it fits, else cut with a note pointing at the full one in the run folder.
    public static string Description(string body)
    {
        const string cut = "\n\n(cut to fit Azure DevOps; the full body is in the run's ledger and .factory/runs/<id>/)";
        return body.Length <= MaxDescription ? body : body[..(MaxDescription - cut.Length)] + cut;
    }

    public static string IdOf(string url) => url.TrimEnd('/')[(url.TrimEnd('/').LastIndexOf('/') + 1)..];

    private async Task<string> Az(CancellationToken ct, params string[] args)
    {
        var r = await ProcessRunner.RunAsync(az, args, repoPath, Timeout, ct: ct);
        if (!r.Succeeded) throw new InvalidOperationException($"az {string.Join(' ', args)} failed:\n{r.Tail()}");
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
