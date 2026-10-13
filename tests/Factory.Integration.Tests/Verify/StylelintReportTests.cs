using Factory.Harness.Verify;
using Xunit;

namespace Factory.Integration.Tests.Verify;

public class StylelintReportTests
{
    private const string Report = """
        [ { "source": "/work/tree/src/ui/Quote.css", "warnings": [
              { "line": 12, "rule": "declaration-strict-value", "text": "Expected a variable for \"color\"" } ] },
          { "source": "/work/tree/src/ui/Clean.css", "warnings": [] } ]
        """;

    [Fact]
    public void Violations_are_relative_to_the_worktree_with_line_and_rule()
    {
        var v = Assert.Single(StylelintReport.Violations(Report, "/work/tree"));
        Assert.Equal(("src/ui/Quote.css", 12, "declaration-strict-value"), (v.File, v.Line, v.Rule));
    }

    [Fact]
    public void A_clean_report_has_no_violations()
    {
        Assert.Empty(StylelintReport.Violations("[]", "/work/tree"));
    }
}
