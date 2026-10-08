using Factory.S2.Quality;

using Xunit;

namespace Factory.Integration.Tests.Quality;

public class CSharpMetricsTests
{
    private const string Source = """
        namespace Pricing;

        public class Rater
        {
            public int Straight(int x) => x + 1;

            public int Branchy(int x, bool a, bool b)
            {
                if (x > 0 && a)
                {
                    foreach (var i in new[] { 1, 2 })
                        if (i > x || b) x += i;
                }
                return x switch { 1 => 2, 2 => 3, _ => 0 };
            }

            public Rater() { }
        }
        """;

    [Fact]
    public void Cyclomatic_complexity_is_one_plus_the_decision_points()
    {
        var fns = CSharpMetrics.Measure("src/Rater.cs", Source);

        Assert.Equal(1, fns.Single(f => f.Name == "Pricing.Rater.Straight").Cyclomatic);
        // if, &&, foreach, if, ||, and two non-default switch arms (the discard arm is the else)
        Assert.Equal(1 + 7, fns.Single(f => f.Name == "Pricing.Rater.Branchy").Cyclomatic);
    }

    [Fact]
    public void A_function_has_its_file_first_line_and_length()
    {
        var f = CSharpMetrics.Measure("src/Rater.cs", Source).Single(f => f.Name == "Pricing.Rater.Branchy");

        Assert.Equal("src/Rater.cs", f.File);
        Assert.Equal(7, f.Line);
        Assert.Equal(9, f.Lines);
    }

    [Fact]
    public void Constructors_are_functions_too()
    {
        Assert.Contains(CSharpMetrics.Measure("src/Rater.cs", Source), f => f.Name == "Pricing.Rater..ctor");
    }
}
