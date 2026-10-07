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
}
