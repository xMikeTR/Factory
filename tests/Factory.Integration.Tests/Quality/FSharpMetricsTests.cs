using Factory.Quality.FSharp;
using Xunit;

namespace Factory.Integration.Tests.Quality;

public class FSharpMetricsTests
{
    private const string Source = """
        module Pricing

        let flat x = x + 1

        let rate (age: int) (smoker: bool) =
            if age > 60 && smoker then 3
            elif age > 40 || smoker then 2
            else
                match age with
                | 18 -> 1
                | 19 -> 1
                | _ -> 0

        type Calc() =
            member _.Total(xs: int list) =
                let mutable t = 0
                for x in xs do
                    while t < x do t <- t + 1
                t

        module Inner =
            let twice f x = f (f x)

        let value = 42
        """;

    private static Factory.Core.FunctionMetrics Fn(string name) => FSharpMetrics.measure("Pricing.fs", Source).Single(f => f.Name == name);

    [Fact]
    public void Every_function_is_measured_and_plain_values_are_not()
    {
        var names = FSharpMetrics.measure("Pricing.fs", Source).Select(f => f.Name).ToHashSet();
        Assert.Equal(new HashSet<string> { "Calc.Total", "Inner.twice", "flat", "rate" }, names);
    }

    [Fact]
    public void Cyclomatic_counts_branches_arms_loops_and_short_circuits_but_not_the_wildcard()
    {
        Assert.Equal(1, Fn("flat").Cyclomatic);
        // 1 + if + && + elif + || + two match arms (the wildcard does not count)
        Assert.Equal(7, Fn("rate").Cyclomatic);
        Assert.Equal(3, Fn("Calc.Total").Cyclomatic); // 1 + for + while
    }

    [Fact]
    public void Line_and_length_come_from_the_binding()
    {
        var rate = Fn("rate");
        Assert.Equal((5, 8), (rate.Line, rate.Lines));
        Assert.Equal("Pricing.fs", rate.File);
    }

    [Fact]
    public void Source_that_does_not_parse_measures_nothing_rather_than_throwing()
    {
        Assert.Empty(FSharpMetrics.measure("Bad.fs", "let let let = = ="));
    }
}
