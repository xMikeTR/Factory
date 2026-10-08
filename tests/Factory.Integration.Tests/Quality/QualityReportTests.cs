using Factory.S2.Quality;
using Xunit;

namespace Factory.Integration.Tests.Quality;

public class QualityReportTests
{
    private static QualityRow Row(string stage, string task, string outcome, string payload) => new(stage, task, outcome, payload);

    [Fact]
    public void The_last_row_per_task_and_stage_decides_so_a_later_clean_attempt_clears_the_finding()
    {
        var section = QualityReport.Section(
        [
            Row("quality", "T-001", "reported", """{"attempt":1,"violations":["- a.cs:3 F: cyclomatic 14"]}"""),
            Row("quality", "T-001", "clean", """{"attempt":2}"""),
            Row("quality", "T-002", "reported", """{"attempt":1,"violations":["- b.cs:9 G: crap 40"]}"""),
            Row("mutation", "T-002", "reported", """{"attempt":1,"survivors":["- b.cs:9 Block (file scored 50%, needs 80%)"]}"""),
        ])!;

        Assert.DoesNotContain("a.cs", section);
        Assert.Contains("T-002", section);
        Assert.Contains("b.cs:9 G: crap 40", section);
        Assert.Contains("b.cs:9 Block", section);
        Assert.Contains("report mode", section);
    }

    [Fact]
    public void Nothing_reported_is_no_section()
    {
        Assert.Null(QualityReport.Section([Row("quality", "T-001", "clean", "{}")]));
    }
}
