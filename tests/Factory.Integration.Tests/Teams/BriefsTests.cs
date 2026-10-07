using Factory.Core;
using Factory.Harness.Registry;
using Factory.Integration.Tests.Support;
using Factory.S2.Teams;
using Xunit;

namespace Factory.Integration.Tests.Teams;

public class BriefsTests
{
    /// live2: the planner chose Testcontainers and `pobserve`, neither of which the sandbox has.
    [Theory]
    [InlineData("planner")]
    [InlineData("task-planner")]
    [InlineData("test-author")]
    [InlineData("implementer")]
    public void Every_role_is_told_what_the_sandbox_can_run(string role)
    {
        var prompt = Briefs.Prompt(role);

        Assert.Contains("no network", prompt);
        Assert.Contains("no Docker daemon", prompt);
        Assert.Contains("pobserve-check <specs-dir> <SpecName> <log file|dir>", prompt);
    }

    /// live3: the planner told the code to log JSON lines, which the PObserve parser cannot read.
    [Fact]
    public void The_planner_is_told_the_line_format_PObserve_parses()
    {
        var prompt = Briefs.Prompt("planner");

        Assert.Contains("`pobserve <event> <payload> key=<id>`", prompt);
        Assert.Contains("pobserve-check db/pmodel/PSpec NoLostUpdate \"$LOGS\"", prompt);
    }

    [Fact]
    public async Task The_logging_contract_names_the_events_and_the_line_format_PObserve_parses()
    {
        var handover = await new DesigncHandoverReader().LoadAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "handover"),
            new S2Config([]), CancellationToken.None);

        var brief = Briefs.Task(handover, Plans.Task("T-004"), [], null, null, null);

        Assert.Contains("`eCommitted`", brief);
        Assert.Contains("`pobserve <event> <payload> key=<id>`", brief);
        Assert.Contains("`pobserve eCommitted 3 key=Q-42`", brief);
    }
}
