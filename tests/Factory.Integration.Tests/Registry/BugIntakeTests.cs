using Factory.Harness.Registry;
using Xunit;

namespace Factory.Integration.Tests.Registry;

public class BugIntakeTests
{
    private const string Spec = "tests/acceptance/quote-form/AC-0107.spec.ts";
    private static readonly BugIntakeRequest Good =
        new("BUG-1", "premium lost on 500", Spec, "spec_gap", "REQ-7", "AC-0107 error state", null, "pipeline");

    private static string TempRegistry()
    {
        var dir = Path.Combine(Path.GetTempPath(), "s2-intake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task A_closed_bug_becomes_an_escape_row_with_its_origin_and_failing_spec()
    {
        var reg = TempRegistry();
        Assert.Null(await BugIntake.RecordAsync(reg, Good, DateTimeOffset.UnixEpoch, CancellationToken.None));

        var line = Assert.Single(await File.ReadAllLinesAsync(Path.Combine(reg, "ledger", "designc.jsonl")));
        using var doc = System.Text.Json.JsonDocument.Parse(line);
        var root = doc.RootElement;
        Assert.Equal("escape", root.GetProperty("kind").GetString());
        Assert.Equal("spec_gap", root.GetProperty("origin").GetString());
        Assert.Equal(Spec, root.GetProperty("traces")[0].GetString());
    }

    [Fact]
    public async Task Recording_the_same_bug_twice_appends_one_row()
    {
        var reg = TempRegistry();
        await BugIntake.RecordAsync(reg, Good, DateTimeOffset.UnixEpoch, CancellationToken.None);
        await BugIntake.RecordAsync(reg, Good, DateTimeOffset.UnixEpoch, CancellationToken.None);
        Assert.Single(await File.ReadAllLinesAsync(Path.Combine(reg, "ledger", "designc.jsonl")));
    }

    [Theory]
    [InlineData("src/Quote.spec.ts", "spec_gap", "REQ-7", "g")]       // not an acceptance spec
    [InlineData(Spec, "typo", "REQ-7", "g")]                          // unknown origin
    [InlineData(Spec, "spec_gap", "", "g")]                           // origin without its reference
    [InlineData(Spec, "spec_gap", "REQ-7", "")]                       // no countermeasure
    public async Task A_bug_that_cannot_close_writes_nothing(string spec, string origin, string reference, string gate)
    {
        var reg = TempRegistry();
        var why = await BugIntake.RecordAsync(reg, Good with { Spec = spec, Origin = origin, Reference = reference, Gate = gate },
            DateTimeOffset.UnixEpoch, CancellationToken.None);
        Assert.NotNull(why);
        Assert.False(File.Exists(Path.Combine(reg, "ledger", "designc.jsonl")));
    }
}
