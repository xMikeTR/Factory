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
    [InlineData("""{"kind":"feature_result","by":"f","feature":"x","commit":null,"outcome":"pass","journeys":[{"journey":"a","entry":"menu","outcome":"pass"}]}""", true)]   // valid
    [InlineData("""{"kind":"feature_result","by":"f","feature":"x","commit":null,"outcome":"pass","journeys":[]}""", false)]   // empty journeys
    [InlineData("""{"kind":"feature_result","by":"f","feature":"x","commit":null,"outcome":"pass","journeys":[{"journey":"a","outcome":"pass"}]}""", false)]   // missing entry
    [InlineData("""{"kind":"feature_result","by":"f","feature":"x","commit":null,"outcome":"pass","journeys":[{"journey":"a","entry":null,"outcome":"pass"}]}""", true)]   // entry null ok
    [InlineData("""{"kind":"feature_result","by":"f","feature":"x","commit":null,"outcome":"pass","journeys":[{"journey":"a","entry":"m","outcome":"meh"}]}""", false)]   // bad outcome
    [InlineData("""{"kind":"feature_result","by":"f","feature":"x","commit":null,"outcome":"fail","journeys":[{"journey":"a","entry":"m","outcome":"fail","failures":"x"}]}""", false)]   // failures not a list
    [InlineData("""{"kind":"feature_result","by":"f","feature":"x","commit":null,"outcome":"fail","journeys":[{"journey":"a","entry":"m","outcome":"fail","failures":[{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"}]}]}""", false)]   // 51 failures
    [InlineData("""{"kind":"feature_result","by":"f","feature":"x","commit":null,"outcome":"fail","journeys":[{"journey":"a","entry":"m","outcome":"fail","failures":[{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"},{"step":0,"kind":"step","msg":"m"}]}]}""", true)]   // 50 failures
    [InlineData("""{"kind":"feature_result","by":"f","feature":"x","commit":null,"outcome":"fail","journeys":[{"journey":"a","entry":"m","outcome":"fail","failures":[{"step":-1,"kind":"step","msg":"m"}]}]}""", false)]   // negative step
    [InlineData("""{"kind":"feature_result","by":"f","feature":"x","commit":null,"outcome":"fail","journeys":[{"journey":"a","entry":"m","outcome":"fail","failures":[{"step":1,"kind":"nope","msg":"m"}]}]}""", false)]   // bad failure kind
    [InlineData("""{"kind":"feature_result","by":"f","feature":"x","commit":null,"outcome":"fail","journeys":[{"journey":"a","entry":"m","outcome":"fail","failures":[{"step":1,"kind":"state"}]}]}""", false)]   // failure without msg
    [InlineData("""{"kind":"feature_result","by":"f","feature":"x","commit":null,"outcome":"fail","journeys":[{"journey":"a","entry":"m","outcome":"fail"}]}""", false)]   // fail names no failure
    [InlineData("""{"kind":"feature_result","by":"f","feature":"x","commit":null,"outcome":"pass","journeys":[{"journey":"a","entry":"m","outcome":"fail","failures":[{"step":1,"kind":"state","msg":"m"}]}]}""", false)]   // row outcome must be fail
    [InlineData("""{"kind":"feature_result","by":"f","feature":"x","commit":null,"outcome":"could_not_run","journeys":[{"journey":"a","entry":"m","outcome":"pass"},{"journey":"b","entry":"m","outcome":"could_not_run"}]}""", true)]   // could_not_run
    public void Journeys_shape_is_enforced(string line, bool valid) => Assert.Equal(valid, DesigncSchema.Validate(line).Count == 0);

    [Theory]
    [InlineData("""{"kind":"feature_result","by":"f","feature":"x","commit":null,"outcome":"pass","journeys":[]}""", "feature_result.journeys: name at least one journey")]
    [InlineData("""{"kind":"feature_result","by":"f","feature":"x","commit":null,"outcome":"pass","journeys":[{"journey":"a","outcome":"pass"}]}""", "feature_result.journeys[0]: must be {journey, entry, outcome}")]
    [InlineData("""{"kind":"feature_result","by":"f","feature":"x","commit":null,"outcome":"fail","journeys":[{"journey":"a","entry":null,"outcome":"pass"}]}""", "feature_result.outcome: must be 'pass' when every journey passed")]
    public void Journeys_shape_messages_match_designc(string line, string message) => Assert.Contains(message, DesigncSchema.Validate(line));

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
