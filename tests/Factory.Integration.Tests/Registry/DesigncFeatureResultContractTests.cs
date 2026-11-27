using Factory.Core;
using Factory.Harness.Registry;
using Factory.Integration.Tests.Support;
using Xunit;

namespace Factory.Integration.Tests.Registry;

/// designc's docs/feature-result.example.json (Fixtures/designc holds a copy, pinned in contracts.lock.json): every row is a
/// valid feature_result for the pinned ledger schema, and the reader turns it into a FeatureResult fact.
/// The schema port (DesigncSchema) does not check designc's `journeys_shape` rule; the enum, required and field checks apply.
public class DesigncFeatureResultContractTests
{
    private static string[] Rows() =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "designc", "feature-result.json")).Where(l => l.Trim().Length > 0).ToArray();

    [Fact]
    public void Every_row_validates_against_the_pinned_ledger_schema()
    {
        Assert.NotEmpty(Rows());
        foreach (var row in Rows()) Assert.Empty(DesigncSchema.Validate(row));
        Assert.Equal("pipeline.jsonl", DesigncSchema.StreamOf("feature_result"));
    }

    [Fact]
    public async Task Every_row_reads_as_a_feature_result_fact()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2-feat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "ledger"));
        await File.WriteAllLinesAsync(Path.Combine(root, "ledger", "pipeline.jsonl"), Rows());

        var reg = await WorkRegistryReader.ReadAsync(root, CancellationToken.None);

        Assert.Equal(Rows().Length, reg.Facts.Count);
        foreach (var (row, fact) in Rows().Zip(reg.Facts))
        {
            var doc = System.Text.Json.JsonDocument.Parse(row).RootElement;
            var f = Assert.IsType<LedgerFact.FeatureResult>(fact.Fact);
            Assert.Equal(doc.GetProperty("feature").GetString(), f.feature);
            Assert.Equal(doc.GetProperty("commit").GetString(), f.commit);
            Assert.Equal(doc.GetProperty("outcome").GetString(), ParityOutcomeModule.toLedger(f.outcome));
        }
    }
}
