using Factory.Harness;

namespace Factory.S2.Git;

/// Branch layout for a run:
///   factory/{run}/integration        one per run, cut from the base branch; the PR head
///   factory/{run}/{team}             one per team, cut from integration at the start of its wave
/// Worktrees live under {stateDir}/worktrees/{run}/... so parallel teams never share a checkout.
/// Every method is idempotent so a restarted run can call it again safely.
public sealed class GitWorkspace(string repoPath, string stateDir)
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _mergeLock = new(1, 1);

    public static string IntegrationBranch(string runId) => $"factory/{runId}/integration";
    public static string TeamBranch(string runId, string teamId) => $"factory/{runId}/{teamId}";

    public string IntegrationPath(string runId) => Path.Combine(stateDir, "worktrees", runId, "integration");
    public string TeamPath(string runId, string teamId) => Path.Combine(stateDir, "worktrees", runId, teamId);

    public async Task<string> EnsureIntegrationAsync(string runId, string baseBranch, CancellationToken ct)
    {
        var path = IntegrationPath(runId);
        if (Directory.Exists(path)) return path;
        var branch = IntegrationBranch(runId);
        if (await BranchExistsAsync(branch, ct))
            await GitAsync(repoPath, ct, "worktree", "add", path, branch);
        else
            await GitAsync(repoPath, ct, "worktree", "add", "-b", branch, path, baseBranch);
        return path;
    }

    public async Task<string> EnsureTeamAsync(string runId, string teamId, CancellationToken ct)
    {
        var path = TeamPath(runId, teamId);
        if (Directory.Exists(path)) return path;
        var branch = TeamBranch(runId, teamId);
        if (await BranchExistsAsync(branch, ct))
            await GitAsync(repoPath, ct, "worktree", "add", path, branch);
        else
            await GitAsync(repoPath, ct, "worktree", "add", "-b", branch, path, IntegrationBranch(runId));
        return path;
    }

    /// Paths changed in the worktree since its last commit, tracked and untracked.
    public async Task<List<string>> ChangedPathsAsync(string worktree, CancellationToken ct)
    {
        var r = await GitAsync(worktree, ct, "status", "--porcelain=v1", "-uall", "-z");
        // -z: entries separated by NUL; a rename entry is followed by its source path as its own entry.
        var entries = r.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var paths = new List<string>();
        for (var i = 0; i < entries.Length; i++)
        {
            var e = entries[i];
            if (e.Length < 4) continue;
            paths.Add(e[3..]);
            if (e[0] is 'R' or 'C') i++; // skip the rename source
        }
        return paths;
    }

    /// Undo changes to specific paths: restore tracked files, delete untracked ones.
    public async Task RevertPathsAsync(string worktree, IEnumerable<string> paths, CancellationToken ct)
    {
        foreach (var p in paths)
        {
            var tracked = await GitAsync(worktree, ct, check: false, "ls-files", "--error-unmatch", "--", p);
            if (tracked.ExitCode == 0)
                await GitAsync(worktree, ct, "checkout", "HEAD", "--", p);
            else
                File.Delete(Path.Combine(worktree, p));
        }
    }

    /// Fingerprint of uncommitted work, used to detect turns that changed nothing.
    public async Task<string> WorkFingerprintAsync(string worktree, CancellationToken ct)
    {
        var diff = await GitAsync(worktree, ct, "diff", "HEAD");
        var status = await GitAsync(worktree, ct, "status", "--porcelain=v1", "-uall");
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(diff.Output + "\n" + status.Output)));
    }

    public async Task<bool> CommitAllAsync(string worktree, string message, CancellationToken ct)
    {
        await GitAsync(worktree, ct, "add", "-A");
        var staged = await GitAsync(worktree, ct, check: false, "diff", "--cached", "--quiet");
        if (staged.ExitCode == 0) return false;
        await GitAsync(worktree, ct, "commit", "-m", message);
        return true;
    }

    /// Merge a finished team branch into integration. Serialised: teams in a wave finish in
    /// parallel, but only one merge touches the integration worktree at a time. Teams never
    /// overlap on declared writes, so a conflict here means something wrote outside its scope.
    public async Task MergeTeamAsync(string runId, string teamId, CancellationToken ct)
    {
        await _mergeLock.WaitAsync(ct);
        try
        {
            var integration = IntegrationPath(runId);
            var branch = TeamBranch(runId, teamId);
            var already = await GitAsync(integration, ct, check: false, "merge-base", "--is-ancestor", branch, "HEAD");
            if (already.ExitCode == 0) return;
            var merge = await GitAsync(integration, ct, check: false, "merge", "--no-ff", "-m", $"factory: merge {teamId}", branch);
            if (merge.ExitCode != 0)
            {
                await GitAsync(integration, ct, check: false, "merge", "--abort");
                throw new InvalidOperationException($"merge of {branch} into integration conflicted:\n{merge.Tail()}");
            }
        }
        finally { _mergeLock.Release(); }
    }

    /// Lines changed and files changed between the base branch and integration.
    public async Task<(int Lines, List<string> Files)> DiffStatsAsync(string runId, string baseBranch, CancellationToken ct)
    {
        var r = await GitAsync(repoPath, ct, "diff", "--numstat", $"{baseBranch}...{IntegrationBranch(runId)}");
        var lines = 0;
        var files = new List<string>();
        foreach (var row in r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var cols = row.Split('\t');
            if (cols.Length < 3) continue;
            lines += (int.TryParse(cols[0], out var a) ? a : 0) + (int.TryParse(cols[1], out var d) ? d : 0);
            files.Add(cols[2]);
        }
        return (lines, files);
    }

    public Task PushAsync(string branch, CancellationToken ct) =>
        GitAsync(repoPath, ct, "push", "--force-with-lease", "origin", branch);

    private async Task<bool> BranchExistsAsync(string branch, CancellationToken ct) =>
        (await GitAsync(repoPath, ct, check: false, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}")).ExitCode == 0;

    private static Task<ProcessResult> GitAsync(string dir, CancellationToken ct, params string[] args) =>
        GitAsync(dir, ct, check: true, args);

    private static async Task<ProcessResult> GitAsync(string dir, CancellationToken ct, bool check, params string[] args)
    {
        var r = await ProcessRunner.RunAsync("git", args, dir, GitTimeout, ct: ct);
        if (check && !r.Succeeded)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed in {dir}:\n{r.Tail()}");
        return r;
    }
}
