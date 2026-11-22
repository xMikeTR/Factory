using Factory.Integration.Tests.Support;
using Xunit;

namespace Factory.Integration.Tests.Support;

public class DesigncSchemaTests
{
    private const string Verdict = """{"kind":"stage_verdict","by":"factory-s2","run":"r","stage":"qa","outcome":"fail","change_ts":1,"ts":5""";

    [Fact]
    public void The_designc_stage_verdict_example_validates_and_streams_to_pipeline()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "designc", "stage-verdict.json");
        foreach (var line in File.ReadAllLines(path).Where(l => l.Trim() != ""))
            Assert.Empty(DesigncSchema.Validate(line));
        Assert.Equal("pipeline.jsonl", DesigncSchema.StreamOf("stage_verdict"));
    }

    [Theory]
    [InlineData(Verdict + "}", true)]
    [InlineData("""{"kind":"stage_verdict","by":"f","run":"r","stage":"qa","outcome":"pass","change_ts":9,"ts":5}""", false)]                       // change after ts
    [InlineData("""{"kind":"stage_verdict","by":"f","run":"r","stage":"qa","outcome":"pass","change_ts":null,"ts":5}""", true)]
    [InlineData("""{"kind":"stage_verdict","by":"f","run":"r","stage":"qa","outcome":"pass","change_ts":1,"round":1}""", false)]                    // round only for review
    [InlineData("""{"kind":"stage_verdict","by":"f","run":"r","stage":"review","outcome":"pass","change_ts":1,"round":0}""", false)]                // round >= 1
    [InlineData("""{"kind":"stage_verdict","by":"f","run":"r","stage":"review","outcome":"pass","change_ts":1,"round":1.5}""", false)]              // whole number
    [InlineData("""{"kind":"stage_verdict","by":"f","run":"r","stage":"review","outcome":"pass","change_ts":1,"round":2}""", true)]
    [InlineData("""{"kind":"stage_verdict","by":"f","run":"r","stage":"holdout","outcome":"fail","change_ts":1,"failures":[{"state":"x","detail":"leak"}]}""", false)] // only 4 keys
    [InlineData("""{"kind":"stage_verdict","by":"f","run":"r","stage":"holdout","outcome":"fail","change_ts":1,"failures":[{"state":3}]}""", false)]
    [InlineData("""{"kind":"stage_verdict","by":"f","run":"r","stage":"holdout","outcome":"fail","change_ts":1,"failures":["x"]}""", false)]
    [InlineData("""{"kind":"stage_verdict","by":"f","run":"r","stage":"holdout","outcome":"fail","change_ts":1,"failures":[{"state":"x","kind":"aria","placement":"p","criterion":"c"}]}""", true)]
    [InlineData("""{"kind":"stage_verdict","by":"f","run":"r","stage":"scenario","outcome":"fail","change_ts":1,"task":"T-3","element":"Orders","scenario":"ORD-1-ts-01","failures":[{"criterion":"ORD-1-ts-01 example 1","kind":"rows"}]}""", true)] // an API scenario: no screen
    [InlineData("""{"kind":"stage_verdict","by":"f","run":"r","stage":"parity","outcome":"fail","change_ts":1}""", false)]                             // stage enum
    public void Verdict_shape_is_enforced(string line, bool valid) => Assert.Equal(valid, DesigncSchema.Validate(line).Count == 0);

    [Theory]
    [InlineData("caught", true)]
    [InlineData("triage", true)]
    [InlineData("nope", false)]
    public void Bucket_member_is_enforced(string bucket, bool valid) =>
        Assert.Equal(valid, DesigncSchema.Validate($$"""{"kind":"escape","by":"f","id":"e","description":"d","bucket":"{{bucket}}","screen":"s"}""").Count == 0);

    [Fact]
    public void A_null_bucket_is_allowed() =>
        Assert.Empty(DesigncSchema.Validate("""{"kind":"escape","by":"f","id":"e","description":"d","bucket":null}"""));
}
