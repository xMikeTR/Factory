using Factory.Core;
using Factory.Integration.Tests.Support;
using Xunit;

namespace Factory.Integration.Tests.Git;

public class GitWorkspaceTests
{
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
}
