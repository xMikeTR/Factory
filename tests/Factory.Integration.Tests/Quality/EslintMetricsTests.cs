using Factory.S2.Quality;
using Xunit;

namespace Factory.Integration.Tests.Quality;

public class EslintMetricsTests
{
    private const string Json = """
        [ { "filePath": "/work/repo/web/src/rate.ts", "messages": [
            { "ruleId": "complexity", "severity": 1, "message": "Function 'rate' has a complexity of 17. Maximum allowed is 0.", "line": 10, "endLine": 59 },
            { "ruleId": "complexity", "severity": 1, "message": "Arrow function has a complexity of 3. Maximum allowed is 0.", "line": 70, "endLine": 74 },
            { "ruleId": "complexity", "severity": 1, "message": "Method 'total' has a complexity of 2. Maximum allowed is 0.", "line": 80, "endLine": 82 },
            { "ruleId": "no-unused-vars", "severity": 1, "message": "x is unused", "line": 1, "endLine": 1 } ] },
          { "filePath": "/work/repo/web/src/clean.ts", "messages": [] } ]
        """;

    [Fact]
    public void Each_complexity_message_becomes_a_function_with_its_lines()
    {
        var fns = EslintMetrics.Parse(Json, "/work/repo");

        Assert.Equal(3, fns.Count);
        var rate = fns.Single(f => f.Name == "rate");
        Assert.Equal(("web/src/rate.ts", 10, 17, 50), (rate.File, rate.Line, rate.Cyclomatic, rate.Lines));
        Assert.Equal(2, fns.Single(f => f.Name == "total").Cyclomatic);
    }

    [Fact]
    public void An_anonymous_function_is_named_by_where_it_starts_so_it_keeps_one_identity_per_file()
    {
        var anon = EslintMetrics.Parse(Json, "/work/repo").Single(f => f.Line == 70);

        Assert.Equal("<anonymous>", anon.Name);
        Assert.Null(anon.Coverage);
    }
}
