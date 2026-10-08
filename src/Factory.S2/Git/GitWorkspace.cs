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

    /// Every file git knows in the worktree (tracked, plus untracked that are not ignored).
    public async Task<List<string>> RepoFilesAsync(string worktree, CancellationToken ct)
    {
        var r = await GitAsync(worktree, ct, "ls-files", "--cached", "--others", "--exclude-standard", "-z");
        return r.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    /// Everything the task has written on its team branch: committed since it was cut from the
    /// integration branch (the TestAuthor's tests), plus what is changed in the worktree now.
    public async Task<List<string>> TaskPathsAsync(string worktree, string runId, CancellationToken ct)
    {
        var committed = await GitAsync(worktree, ct, "diff", "--name-only", $"{IntegrationBranch(runId)}...HEAD");
        return committed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Concat(await ChangedPathsAsync(worktree, ct))
            .Distinct().ToList();
    }

    /// The task's whole diff against the integration branch, committed and uncommitted, new files
    /// included (staged as intent-to-add for git to see them, then unstaged). Factory state is left out.
    public async Task<string> TaskDiffAsync(string worktree, string runId, CancellationToken ct)
    {
        await GitAsync(worktree, ct, "add", "-A", "-N");
        var r = await GitAsync(worktree, ct, "diff", IntegrationBranch(runId), "--", ".", ":(exclude).factory");
        await GitAsync(worktree, ct, "reset", "-q"); // an index entry would make RevertPathsAsync think a new file is tracked
        return r.Output;
    }

    /// The worktree as a git tree (what `commit -a` would record), so a later review can ask what the
    /// fix changed since this one. Written into the object store only; no commit, no ref.
    public async Task<string> SnapshotAsync(string worktree, CancellationToken ct)
    {
        await GitAsync(worktree, ct, "add", "-A");
        var tree = (await GitAsync(worktree, ct, "write-tree")).Output.Trim();
        await GitAsync(worktree, ct, "reset", "-q");
        return tree;
    }

    /// What changed in the worktree since `tree` (a SnapshotAsync result), new files included.
    public async Task<string> DiffSinceAsync(string worktree, string tree, CancellationToken ct)
    {
        await GitAsync(worktree, ct, "add", "-A");
        var diff = (await GitAsync(worktree, ct, "diff", "--cached", tree, "--", ".", ":(exclude).factory")).Output;
        await GitAsync(worktree, ct, "reset", "-q");
        return diff;
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

    /// Checks leave the worktree as they found it: disposing reverts every path that was not
    /// changed before (generated sources, logs), so only what the agents wrote is ever committed.
    /// A path already changed before is left alone, even if the check rewrote it.
    /// Directories the check created are removed whole, git-ignored contents included (`p compile`
    /// generates a project with its own obj/, which the project around it would compile); build
    /// caches (bin/, obj/, node_modules/) are kept so the next build stays incremental.
    public async Task<IAsyncDisposable> LeaveAsFoundAsync(string worktree, CancellationToken ct)
    {
        var before = (await ChangedPathsAsync(worktree, ct)).ToHashSet();
        var dirs = Directories(worktree).ToHashSet();
        return new Restore(async () =>
        {
            var added = (await ChangedPathsAsync(worktree, CancellationToken.None)).Where(p => !before.Contains(p)).ToList();
            await RevertPathsAsync(worktree, added, CancellationToken.None);
            var created = Directories(worktree).Where(d => !dirs.Contains(d)).ToList();
            foreach (var d in created.Where(d => Directory.Exists(d) && !BuildCaches.Contains(Path.GetFileName(d))))
                Directory.Delete(d, recursive: true);
            return added.Count > 0 || created.Count > 0;
        });
    }

    private static readonly HashSet<string> BuildCaches = ["bin", "obj", "node_modules"];

    /// Every directory in the worktree, not descending into .git or build caches. A directory
    /// is only yielded after its parent, so a created tree is removed from its top.
    private static IEnumerable<string> Directories(string root)
    {
        var pending = new Queue<string>([root]);
        while (pending.Count > 0)
            foreach (var d in Directory.EnumerateDirectories(pending.Dequeue()))
            {
                yield return d;
                if (Path.GetFileName(d) is not ".git" && !BuildCaches.Contains(Path.GetFileName(d))) pending.Enqueue(d);
            }
    }

    /// Take the implementation out of the worktree for the stashed-impl check: copy `paths` aside
    /// under the state dir, then revert them to HEAD. Disposing puts them back. Not `git stash`:
    /// the stash is shared by every worktree of the repo and teams run in parallel. A crash
    /// between the two leaves the copy and its manifest behind for RestoreStashAsync.
    public async Task<IAsyncDisposable> StashImplAsync(string worktree, IReadOnlyCollection<string> paths, string key, CancellationToken ct)
    {
        var dir = StashDir(key);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        var files = Path.Combine(dir, "files");
        Directory.CreateDirectory(files);
        var manifest = new List<StashEntry>();
        foreach (var p in paths)
        {
            var src = Path.Combine(worktree, p);
            var exists = File.Exists(src);
            if (exists)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(files, p))!);
                File.Copy(src, Path.Combine(files, p));
            }
            manifest.Add(new StashEntry(p, exists));
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), System.Text.Json.JsonSerializer.Serialize(manifest), ct);
        await RevertPathsAsync(worktree, paths, ct);
        return new Restore(() => RestoreStashAsync(worktree, key, CancellationToken.None));
    }

    /// Put back a stash left by StashImplAsync. False when there is none.
    public async Task<bool> RestoreStashAsync(string worktree, string key, CancellationToken ct)
    {
        var dir = StashDir(key);
        var manifestPath = Path.Combine(dir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); // crashed before the revert: nothing was taken
            return false;
        }
        var manifest = System.Text.Json.JsonSerializer.Deserialize<List<StashEntry>>(await File.ReadAllTextAsync(manifestPath, ct))!;
        foreach (var e in manifest)
        {
            var dst = Path.Combine(worktree, e.Path);
            if (e.Existed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(Path.Combine(dir, "files", e.Path), dst, overwrite: true);
            }
            else if (File.Exists(dst)) File.Delete(dst);
        }
        Directory.Delete(dir, recursive: true);
        return true;
    }

    private string StashDir(string key) => Path.Combine(stateDir, "stash", key);

    private sealed record StashEntry(string Path, bool Existed);

    private sealed class Restore(Func<Task<bool>> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
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

    /// Commit only `paths` (the TestAuthor's tests), leaving any other change in the worktree.
    public async Task<bool> CommitPathsAsync(string worktree, IReadOnlyCollection<string> paths, string message, CancellationToken ct)
    {
        if (paths.Count == 0) return false;
        await GitAsync(worktree, ct, ["add", "-A", "--", .. paths]);
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
