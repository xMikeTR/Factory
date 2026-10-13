using Factory.Harness.Registry;
using Xunit;

namespace Factory.Integration.Tests.Registry;

public class WorkRegistryReaderTests
{
    private static string Registry(Dictionary<string, string> files)
    {
        var root = Path.Combine(Path.GetTempPath(), "s2-work-" + Guid.NewGuid().ToString("N"));
        foreach (var (rel, text) in files)
        {
            var path = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        return root;
    }

    private const string Requirement = "id: REQ-1\nelements:\n  - Pricing/Form\n";
    private const string Work = """
        id: W-1
        requirement: REQ-1
        elements: [Pricing/Form]
        writes: [src/Form/**]
        depends_on: []
        done_when:
          - gate: dotnet test
        """;

    [Fact]
    public async Task A_valid_work_file_becomes_a_work_item()
    {
        var root = Registry(new() { ["requirements/REQ-1.yaml"] = Requirement, ["work/W-1.yaml"] = Work });

        var reg = await WorkRegistryReader.ReadAsync(root, CancellationToken.None);

        Assert.Empty(reg.Problems);
        Assert.Equal("W-1", Assert.Single(reg.Items).Id.Value);
    }

    [Fact]
    public async Task Problems_are_plain_sentences_and_the_item_is_left_out()
    {
        var root = Registry(new()
        {
            ["requirements/REQ-1.yaml"] = Requirement,
            ["work/W-1.yaml"] = Work.Replace("REQ-1", "REQ-9"),
            ["work/W-2.yaml"] = Work.Replace("W-1", "W-2").Replace("depends_on: []", "depends_on: [W-7]").Replace("Pricing/Form]", "Pricing/Other]"),
        });

        var reg = await WorkRegistryReader.ReadAsync(root, CancellationToken.None);

        Assert.Empty(reg.Items);
        Assert.Contains("W-1: requirement REQ-9 does not exist in requirements/.", reg.Problems);
        Assert.Contains("W-2: depends_on W-7 is not a work item.", reg.Problems);
        Assert.Contains(reg.Problems, p => p.StartsWith("W-2: Pricing/Other is not an element of requirement REQ-1."));
    }

    [Fact]
    public async Task The_digest_reads_the_ledger_and_lists_a_failed_gate()
    {
        var root = Registry(new()
        {
            ["requirements/REQ-1.yaml"] = Requirement,
            ["work/W-1.yaml"] = Work,
            ["ledger/designc.jsonl"] = """{"kind":"item_pulled","item":"W-1","ts":1,"by":"mike"}""" + "\n",
            ["ledger/checks.jsonl"] =
                """{"kind":"item_gate","item":"W-1","gate":"dotnet test","outcome":"fail","source":"local","commit":"abcdef123","dirty":false,"ts":2,"by":"mike"}""" + "\n",
        });

        var (digest, problems) = await WorkDigestReport.BuildAsync(root, null, "main", 0, 3, CancellationToken.None);

        Assert.Empty(problems);
        Assert.Contains("W-1: gate \"dotnet test\" failed at abcdef1", WorkDigestReport.Render(digest, problems));
    }

    [Fact]
    public async Task Recheck_and_escape_rows_reach_the_metrics()
    {
        var root = Registry(new()
        {
            ["requirements/REQ-1.yaml"] = Requirement,
            ["work/W-1.yaml"] = Work,
            ["ledger/designc.jsonl"] =
                """{"kind":"item_recheck","item":"W-1","handover":"h","stale":true,"reasons":["moved"],"ts":1,"by":"mike"}""" + "\n" +
                """{"kind":"escape","id":"BUG-1","description":"x","origin":"spec_gap","ts":2,"by":"s2"}""" + "\n" +
                """{"kind":"item_conflict_deferred","item":"W-1","ts":3,"by":"s2"}""" + "\n",
        });

        var m = await WorkDigestReport.MetricsAsync(root, null, "main", CancellationToken.None);

        Assert.Equal(1, m.ConflictDeferrals);
        Assert.Equal(1.0, m.RecheckRate.Value);
        Assert.Equal([("spec_gap", 1)], m.EscapesByOrigin.Select(e => (e.Item1, e.Item2)));
    }
}
