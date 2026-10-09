using System.Text.Json;
using Factory.Core;
using Xunit;

namespace Factory.Integration.Tests.Registry;

/// designc checks d.code before a handover with its own port of PathPattern (DC000, DC009). Its contract
/// cases live in designc's docs/path-patterns.json; Fixtures/designc holds a copy. If one of these fails,
/// designc and the factory disagree on what a code area means: change both sides and the fixture together.
public class DesigncPathPatternContractTests
{
    private static readonly JsonElement Fixture =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "designc", "path-patterns.json"))).RootElement;

    private static PathPattern P(string s) => PathPatternModule.createOrFail(s);

    [Fact]
    public void Create_accepts_normalises_and_rejects_as_designc_does()
    {
        foreach (var c in Fixture.GetProperty("create").EnumerateArray())
        {
            var input = c.GetProperty("input").GetString()!;
            var r = PathPatternModule.create(input);
            if (c.TryGetProperty("valid", out var v) && !v.GetBoolean())
                Assert.True(r.IsError, $"should reject '{input}'");
            else
            {
                Assert.True(r.IsOk, $"should accept '{input}'");
                Assert.Equal(c.GetProperty("value").GetString(), r.ResultValue.Value);
            }
        }
    }

    [Fact]
    public void MayOverlap_agrees_with_designc_both_ways()
    {
        foreach (var c in Fixture.GetProperty("mayOverlap").EnumerateArray())
        {
            var (a, b, want) = (c.GetProperty("a").GetString()!, c.GetProperty("b").GetString()!, c.GetProperty("overlap").GetBoolean());
            Assert.True(want == PathPatternModule.mayOverlap(P(a), P(b)), $"{a} vs {b}: expected {want}");
            Assert.True(want == PathPatternModule.mayOverlap(P(b), P(a)), $"{b} vs {a}: expected {want}");
        }
    }

    [Fact]
    public void Matches_agrees_with_designc()
    {
        foreach (var c in Fixture.GetProperty("matches").EnumerateArray())
        {
            var (pattern, path, want) = (c.GetProperty("pattern").GetString()!, c.GetProperty("path").GetString()!, c.GetProperty("match").GetBoolean());
            Assert.True(want == PathPatternModule.matches(P(pattern), path), $"{pattern} vs {path}: expected {want}");
        }
    }
}
