using Factory.Core;
using Factory.Harness.Registry;
using Factory.Integration.Tests.Support;
using Factory.S2.Teams;
using Xunit;

namespace Factory.Integration.Tests.Teams;

public class BriefsTests
{
    [Fact]
    public async Task The_logging_contract_names_the_events_and_the_line_format_PObserve_parses()
    {
        var handover = await new DesigncHandoverReader().LoadAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "handover"),
            new S2Config([]), CancellationToken.None);

        var brief = Briefs.Task(handover, Plans.Task("T-004"), [], null, null, null);

        Assert.Contains("`eCommitted`", brief);
        Assert.Contains("`pobserve <event> <payload>`", brief);
        Assert.Contains("`pobserve eCommitted 3`", brief);
    }
}
