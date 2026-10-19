using Factory.Harness;
using Xunit;

namespace Factory.Integration.Tests.Workspace;

public class FastCheckTests
{
    private static FastCheck.Part Fast(string name, params string[] lines) => new(name, _ => Task.FromResult<IReadOnlyList<string>>(lines));
    private static FastCheck.Part Slow(string name) => new(name, async ct => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return []; });

    [Fact]
    public async Task Findings_of_every_part_are_listed_in_order()
    {
        var text = await FastCheck.RunAsync([Fast("quality", "- A.cs:3 F: Cyclomatic 9"), Fast("rules", "- B.cs forbidden")], TimeSpan.FromSeconds(3), default);
        Assert.Equal("- A.cs:3 F: Cyclomatic 9\n- B.cs forbidden", text);
    }

    [Fact]
    public async Task Nothing_found_says_so()
    {
        Assert.Equal("no findings", await FastCheck.RunAsync([Fast("quality")], TimeSpan.FromSeconds(3), default));
    }

    [Fact]
    public async Task A_part_over_the_budget_is_named_as_not_run_and_the_others_still_report()
    {
        var started = DateTime.UtcNow;
        var text = await FastCheck.RunAsync([Fast("quality", "- A.cs:3 F: Cyclomatic 9"), Slow("namespaces"), Fast("scope", "- C.cs is outside your write paths")], TimeSpan.FromMilliseconds(300), default);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.Contains("- A.cs:3 F: Cyclomatic 9", text);
        Assert.Contains("not run: namespaces took more than", text);
        Assert.Contains("verify runs it", text);
        Assert.Contains("- C.cs is outside your write paths", text);
    }

    [Fact]
    public async Task A_cancelled_turn_still_cancels()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FastCheck.RunAsync([Slow("namespaces")], TimeSpan.FromMinutes(1), cts.Token));
    }

    [Fact]
    public async Task A_part_that_throws_is_reported_not_fatal()
    {
        var boom = new FastCheck.Part("rules", _ => throw new InvalidOperationException("bad config"));
        var text = await FastCheck.RunAsync([boom, Fast("quality", "- A.cs:3 F")], TimeSpan.FromSeconds(3), default);
        Assert.Contains("not run: rules failed (bad config)", text);
        Assert.Contains("- A.cs:3 F", text);
    }
}
