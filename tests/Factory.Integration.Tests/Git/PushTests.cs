using Factory.Harness;
using Factory.S2.Git;
using Xunit;

namespace Factory.Integration.Tests.Git;

/// PushAsync uses --force-with-lease: it may replace the run branch it pushed before, but never a remote commit it has not seen.
public class PushTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("s2-push-").FullName;
    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    private static async Task<string> Git(string dir, params string[] args)
    {
        var r = await ProcessRunner.RunAsync("git", args, dir, TimeSpan.FromMinutes(1));
        Assert.True(r.Succeeded, $"git {string.Join(' ', args)}: {r.Tail()}");
        return r.Output.Trim();
    }

    private async Task<(string repo, string remote)> Setup()
    {
        var remote = Directory.CreateDirectory(Path.Combine(_root, "remote.git")).FullName;
        await Git(remote, "init", "-q", "--bare", "-b", "main");
        var repo = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
        await Git(repo, "init", "-q", "-b", "main");
        await Git(repo, "config", "user.email", "t@t");
        await Git(repo, "config", "user.name", "t");
        await Git(repo, "remote", "add", "origin", remote);
        await Git(repo, "commit", "-q", "--allow-empty", "-m", "base");
        await Git(repo, "checkout", "-q", "-b", "run");
        await Git(repo, "commit", "-q", "--allow-empty", "-m", "one");
        await Git(repo, "push", "-q", "origin", "run");
        return (repo, remote);
    }

    [Fact]
    public async Task A_rewritten_run_branch_replaces_the_one_this_clone_pushed()
    {
        var (repo, remote) = await Setup();
        await Git(repo, "commit", "-q", "--amend", "--allow-empty", "-m", "one, reworded");

        await new GitWorkspace(repo, Path.Combine(_root, "state")).PushAsync("run", CancellationToken.None);

        Assert.Equal(await Git(repo, "rev-parse", "run"), await Git(remote, "rev-parse", "run"));
    }

    [Fact]
    public async Task A_remote_commit_this_clone_has_not_fetched_is_not_overwritten()
    {
        var (repo, remote) = await Setup();
        var other = Path.Combine(_root, "other");
        await Git(_root, "clone", "-q", remote, other);
        await Git(other, "config", "user.email", "o@o");
        await Git(other, "config", "user.name", "o");
        await Git(other, "checkout", "-q", "run");
        await Git(other, "commit", "-q", "--allow-empty", "-m", "someone else");
        await Git(other, "push", "-q", "origin", "run");
        var theirs = await Git(remote, "rev-parse", "run");
        await Git(repo, "commit", "-q", "--amend", "--allow-empty", "-m", "mine");

        await Assert.ThrowsAnyAsync<Exception>(() => new GitWorkspace(repo, Path.Combine(_root, "state")).PushAsync("run", CancellationToken.None));

        Assert.Equal(theirs, await Git(remote, "rev-parse", "run"));
    }
}
