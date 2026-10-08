using Factory.Core;
using Factory.S2.Quality;
using Xunit;

namespace Factory.Integration.Tests.Quality;

public class CoberturaTests
{
    private const string Xml = """
        <coverage><packages><package><classes>
          <class filename="/work/repo/src/Rater.cs"><lines>
            <line number="7" hits="3"/><line number="8" hits="3"/><line number="9" hits="0"/><line number="10" hits="0"/>
            <line number="20" hits="1"/>
          </lines></class>
        </classes></package></packages></coverage>
        """;

    private static FunctionMetrics Fn(string file, int line, int lines) => new(file, "F", line, 1, lines, null);

    [Fact]
    public void A_function_is_covered_by_the_share_of_its_coverable_lines_that_ran()
    {
        var joined = Cobertura.Join([Fn("src/Rater.cs", 7, 5)], Xml); // lines 7-11: four coverable, two ran

        Assert.Equal(0.5, joined.Single().Coverage);
    }

    [Fact]
    public void A_function_with_no_coverable_lines_in_the_report_has_no_coverage_data()
    {
        Assert.Null(Cobertura.Join([Fn("src/Rater.cs", 30, 3)], Xml).Single().Coverage);
        Assert.Null(Cobertura.Join([Fn("src/Other.cs", 7, 5)], Xml).Single().Coverage);
    }
}
