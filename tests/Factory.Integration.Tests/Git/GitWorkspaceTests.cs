using Factory.Core;
using Factory.Integration.Tests.Support;
using Xunit;

namespace Factory.Integration.Tests.Git;

public class GitWorkspaceTests
{
    [Fact]
    public async Task A_baseline_checkout_is_the_integration_branch_apart_from_the_team_worktree_and_is_removed_after()
    {
        using var h = await TeamHarness.CreateAsync(new ScriptedChatClient(), new Limits(3, 2));
        var wt = await h.Services.Git.EnsureTeamAsync(TeamHarness.RunId, "team-x", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(wt, "src/Pricing.Data/Quote.cs"), "// changed\n");
        string path;

        await using (var baseline = await h.Services.Git.BaselineCheckoutAsync(TeamHarness.RunId, CancellationToken.None))
        {
            path = baseline.Path;
            Assert.NotEqual(wt, path);
            Assert.Equal("// quote\n", await File.ReadAllTextAsync(Path.Combine(path, "src/Pricing.Data/Quote.cs")));
        }

        Assert.False(Directory.Exists(path));
        Assert.Equal("// changed\n", await File.ReadAllTextAsync(Path.Combine(wt, "src/Pricing.Data/Quote.cs")));
    }

    [Fact]
    public async Task StashImpl_hides_the_implementation_and_restores_it()
    {
        using var h = await TeamHarness.CreateAsync(new ScriptedChatClient(), new Limits(3, 2));
        var wt = await h.Services.Git.EnsureTeamAsync(TeamHarness.RunId, "team-x", CancellationToken.None);
        var quote = Path.Combine(wt, "src/Pricing.Data/Quote.cs");
        var added = Path.Combine(wt, "src/Pricing.Data/QuoteStatus.cs");
        var test = Path.Combine(wt, "tests/Pricing.Data.Tests/new_test.sh");
        await File.WriteAllTextAsync(quote, "// changed\n");
        await File.WriteAllTextAsync(added, "enum QuoteStatus {}\n");
        await File.WriteAllTextAsync(test, "true\n");

        await using (await h.Services.Git.StashImplAsync(wt, ["src/Pricing.Data/Quote.cs", "src/Pricing.Data/QuoteStatus.cs"], "run-test/team-x", CancellationToken.None))
        {
            Assert.Equal("// quote\n", await File.ReadAllTextAsync(quote));
            Assert.False(File.Exists(added));
            Assert.True(File.Exists(test)); // tests stay
        }

        Assert.Equal("// changed\n", await File.ReadAllTextAsync(quote));
        Assert.True(File.Exists(added));
    }

    [Fact]
    public async Task A_stash_left_by_a_crash_is_restored()
    {
        using var h = await TeamHarness.CreateAsync(new ScriptedChatClient(), new Limits(3, 2));
        var wt = await h.Services.Git.EnsureTeamAsync(TeamHarness.RunId, "team-x", CancellationToken.None);
        var added = Path.Combine(wt, "src/Pricing.Data/QuoteStatus.cs");
        await File.WriteAllTextAsync(added, "enum QuoteStatus {}\n");

        await h.Services.Git.StashImplAsync(wt, ["src/Pricing.Data/QuoteStatus.cs"], "run-test/team-x", CancellationToken.None); // never disposed
        Assert.False(File.Exists(added));

        Assert.True(await h.Services.Git.RestoreStashAsync(wt, "run-test/team-x", CancellationToken.None));
        Assert.True(File.Exists(added));
        Assert.False(await h.Services.Git.RestoreStashAsync(wt, "run-test/team-x", CancellationToken.None));
    }

    /// live2: `p compile` generated a project whose obj/ is git-ignored, so it survived the check
    /// and the test project around it compiled its AssemblyInfo.cs twice.
    [Fact]
    public async Task LeaveAsFound_removes_the_directories_a_check_created_but_keeps_build_caches()
    {
        using var h = await TeamHarness.CreateAsync(new ScriptedChatClient(), new Limits(3, 2));
        var wt = await h.Services.Git.EnsureTeamAsync(TeamHarness.RunId, "team-x", CancellationToken.None);
        var generated = Path.Combine(wt, "tests/Pricing.Data.Tests/PModel/PGenerated");
        var cache = Path.Combine(wt, "tests/Pricing.Data.Tests/bin");

        await using (await h.Services.Git.LeaveAsFoundAsync(wt, CancellationToken.None))
        {
            Directory.CreateDirectory(Path.Combine(generated, "bin/Release"));
            await File.WriteAllTextAsync(Path.Combine(generated, "bin/Release/Gen.AssemblyInfo.cs"), "// ignored\n");
            Directory.CreateDirectory(cache);
            await File.WriteAllTextAsync(Path.Combine(cache, "Pricing.Data.Tests.dll"), "cache\n");
        }

        Assert.False(Directory.Exists(Path.Combine(wt, "tests/Pricing.Data.Tests/PModel")));
        Assert.True(File.Exists(Path.Combine(cache, "Pricing.Data.Tests.dll")));
    }

    [Fact]
    public async Task TaskPaths_are_what_the_task_wrote_committed_or_not()
    {
        using var h = await TeamHarness.CreateAsync(new ScriptedChatClient(), new Limits(3, 2));
        var git = h.Services.Git;
        var wt = await git.EnsureTeamAsync(TeamHarness.RunId, "team-x", CancellationToken.None);
        Assert.Empty(await git.TaskPathsAsync(wt, TeamHarness.RunId, CancellationToken.None));

        await File.WriteAllTextAsync(Path.Combine(wt, "tests/Pricing.Data.Tests/new_test.sh"), "true\n");
        await git.CommitPathsAsync(wt, ["tests/Pricing.Data.Tests/new_test.sh"], "tests", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(wt, "src/Pricing.Data/Quote.cs"), "// changed\n");

        var paths = await git.TaskPathsAsync(wt, TeamHarness.RunId, CancellationToken.None);
        Assert.Equal(["src/Pricing.Data/Quote.cs", "tests/Pricing.Data.Tests/new_test.sh"], paths.Order(StringComparer.Ordinal));
    }

    /// live run 20261015-161547: the tests commit held obj/ (the new repo had no .gitignore), every
    /// `dotnet test` then changed those tracked files, scope enforcement reverted them and done never counted.
    /// Build output (bin, obj, node_modules) is never a changed path, never committed and never work.
    [Fact]
    public async Task Build_output_is_never_a_changed_path_or_committed_even_when_already_tracked()
    {
        using var h = await TeamHarness.CreateAsync(new ScriptedChatClient(), new Limits(3, 2));
        var git = h.Services.Git;
        var wt = await git.EnsureTeamAsync(TeamHarness.RunId, "team-x", CancellationToken.None);
        var trackedObj = Path.Combine(wt, "tests/Pricing.Data.Tests/obj/project.assets.json");
        Directory.CreateDirectory(Path.GetDirectoryName(trackedObj)!);
        await File.WriteAllTextAsync(trackedObj, "{}\n");
        await h.GitIn(wt, "add", "-f", "tests/Pricing.Data.Tests/obj/project.assets.json");
        await h.GitIn(wt, "commit", "-q", "-m", "obj committed by mistake");
        var fingerprint = await git.WorkFingerprintAsync(wt, CancellationToken.None);

        await File.WriteAllTextAsync(trackedObj, "{\"changed\": true}\n");
        Directory.CreateDirectory(Path.Combine(wt, "src/Pricing.Data/obj"));
        await File.WriteAllTextAsync(Path.Combine(wt, "src/Pricing.Data/obj/Quote.dll"), "new\n");
        Directory.CreateDirectory(Path.Combine(wt, "web/node_modules/x"));
        await File.WriteAllTextAsync(Path.Combine(wt, "web/node_modules/x/index.js"), "new\n");

        Assert.Empty(await git.ChangedPathsAsync(wt, CancellationToken.None));
        Assert.Equal(fingerprint, await git.WorkFingerprintAsync(wt, CancellationToken.None));
        Assert.False(await git.CommitAllAsync(wt, "nothing to commit", CancellationToken.None));

        await File.WriteAllTextAsync(Path.Combine(wt, "src/Pricing.Data/Quote.cs"), "// changed\n");
        Assert.Equal(["src/Pricing.Data/Quote.cs"], await git.ChangedPathsAsync(wt, CancellationToken.None));
        Assert.True(await git.CommitAllAsync(wt, "work", CancellationToken.None));
        var committed = (await h.GitIn(wt, "show", "--name-only", "--format=", "HEAD")).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["src/Pricing.Data/Quote.cs"], committed);
    }

    [Fact]
    public async Task Build_output_is_not_in_a_task_s_paths_or_diff()
    {
        using var h = await TeamHarness.CreateAsync(new ScriptedChatClient(), new Limits(3, 2));
        var git = h.Services.Git;
        var wt = await git.EnsureTeamAsync(TeamHarness.RunId, "team-x", CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(wt, "tests/Pricing.Data.Tests/obj"));
        await File.WriteAllTextAsync(Path.Combine(wt, "tests/Pricing.Data.Tests/obj/a.cache"), "x\n");
        await h.GitIn(wt, "add", "-f", "tests/Pricing.Data.Tests/obj/a.cache");
        await h.GitIn(wt, "commit", "-q", "-m", "tests with obj");
        await File.WriteAllTextAsync(Path.Combine(wt, "tests/Pricing.Data.Tests/obj/a.cache"), "y\n");
        await File.WriteAllTextAsync(Path.Combine(wt, "src/Pricing.Data/Quote.cs"), "// changed\n");

        Assert.Equal(["src/Pricing.Data/Quote.cs"], await git.TaskPathsAsync(wt, TeamHarness.RunId, CancellationToken.None));
        var diff = await git.TaskDiffAsync(wt, TeamHarness.RunId, CancellationToken.None);
        Assert.Contains("Quote.cs", diff);
        Assert.DoesNotContain("a.cache", diff);
        var tree = await git.SnapshotAsync(wt, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(wt, "tests/Pricing.Data.Tests/obj/a.cache"), "z\n");
        Assert.Equal("", await git.DiffSinceAsync(wt, tree, CancellationToken.None));
    }
}
