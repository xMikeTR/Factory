using Factory.Core;
using Factory.S2.Git;
using Xunit;

namespace Factory.Integration.Tests.Git;

/// A brand-new project has no repository yet: `run --repo` creates one with an empty baseline commit on the
/// base branch, and never touches a folder that already holds files.
public class NewRepoTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("s2-newrepo-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static async Task<string> Git(string dir, params string[] args)
    {
        var r = await Factory.Harness.ProcessRunner.RunAsync("git", args, dir, TimeSpan.FromMinutes(1));
        Assert.True(r.Succeeded, $"git {string.Join(' ', args)}: {r.Tail()}");
        return r.Output.Trim();
    }

    private static async Task AssertBaseline(string repo, string branch)
    {
        Assert.Equal("1", await Git(repo, "rev-list", "--count", branch));
        // The baseline ignores build output, so a first build never commits bin/ or obj/ (live run 20261015-161547).
        Assert.Equal(".gitignore", (await Git(repo, "ls-tree", "-r", "--name-only", branch)).Trim());
        var ignore = await Git(repo, "show", $"{branch}:.gitignore");
        foreach (var d in new[] { "bin/", "obj/", "node_modules/" }) Assert.Contains(d, ignore);
    }

    [Fact]
    public async Task A_missing_folder_becomes_a_repository_with_an_empty_baseline_on_the_base_branch()
    {
        var repo = Path.Combine(_root, "game");

        var note = await GitWorkspace.EnsureRepoAsync(repo, "main", CancellationToken.None);

        Assert.Contains("created", note);
        await AssertBaseline(repo, "main");
        var integration = await new GitWorkspace(repo, Path.Combine(_root, "state")).EnsureIntegrationAsync("r1", "main", CancellationToken.None);
        Assert.True(Directory.Exists(integration), "a run can branch from the new baseline");
    }

    [Fact]
    public async Task An_empty_folder_becomes_a_repository()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_root, "empty")).FullName;

        Assert.NotNull(await GitWorkspace.EnsureRepoAsync(repo, "trunk", CancellationToken.None));

        await AssertBaseline(repo, "trunk");
    }

    [Fact]
    public async Task A_repository_with_no_commit_yet_gets_the_baseline()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_root, "unborn")).FullName;
        await Git(repo, "init", "-q", "-b", "master");

        Assert.NotNull(await GitWorkspace.EnsureRepoAsync(repo, "main", CancellationToken.None));

        await AssertBaseline(repo, "main");
    }

    [Fact]
    public async Task An_existing_repository_is_left_alone()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_root, "existing")).FullName;
        await Git(repo, "init", "-q", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "a\n");
        await Git(repo, "add", "-A");
        await Git(repo, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-qm", "a");
        var head = await Git(repo, "rev-parse", "HEAD");

        Assert.Null(await GitWorkspace.EnsureRepoAsync(repo, "main", CancellationToken.None));

        Assert.Equal(head, await Git(repo, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task A_folder_with_files_that_is_not_a_repository_is_refused_and_untouched()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_root, "loose")).FullName;
        await File.WriteAllTextAsync(Path.Combine(repo, "notes.txt"), "mine\n");

        var e = await Assert.ThrowsAsync<FactoryRejectedException>(() => GitWorkspace.EnsureRepoAsync(repo, "main", CancellationToken.None));

        Assert.Contains("not a git repository", string.Join(" ", e.Errors));
        Assert.False(Directory.Exists(Path.Combine(repo, ".git")));
    }
}
