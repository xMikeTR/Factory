using Factory.Core;
using Factory.Harness.Registry;
using Xunit;

namespace Factory.Integration.Tests.Registry;

public class JiraMirrorReportTests
{
    // REQ-1 is a Jira-style key, so W-1 mirrors to the issue REQ-1.
    private static string Registry(string ledger)
    {
        var root = Path.Combine(Path.GetTempPath(), "s2-jira-" + Guid.NewGuid().ToString("N"));
        void Put(string rel, string text)
        {
            var p = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }
        Put("requirements/REQ-1.yaml", "id: REQ-1\nelements:\n  - Pricing/Form\n");
        Put("work/W-1.yaml", "id: W-1\nrequirement: REQ-1\nelements: [Pricing/Form]\nwrites: [src/Form/**]\ndepends_on: []\ndone_when:\n  - gate: dotnet test\n");
        Put("ledger/designc.jsonl", ledger);
        return root;
    }

    private const string Pulled =
        """{"kind":"handover","handover":"h1","revision":"r1","item":"W-1","ts":1,"by":"mike"}""" + "\n" +
        """{"kind":"item_pulled","item":"W-1","ts":2,"by":"mike"}""" + "\n";

    private static Task<IReadOnlyList<MirrorAction>> Plan(string root, string jira, string mirrored) =>
        JiraMirrorReport.PlanAsync(root, null, "main",
            new Dictionary<string, string> { ["REQ-1"] = jira },
            mirrored.Length == 0 ? new Dictionary<string, string>() : new Dictionary<string, string> { ["REQ-1"] = mirrored },
            default);

    [Fact]
    public async Task An_active_item_moves_a_to_do_issue_to_in_progress()
    {
        var plan = await Plan(Registry(Pulled), "To Do", "To Do");

        var t = Assert.IsType<MirrorAction.Transition>(Assert.Single(plan));
        Assert.Equal(("REQ-1", "In Progress"), (t.key, t.target));
    }

    [Fact]
    public async Task A_status_edited_in_Jira_is_flagged_once_and_never_planned_as_a_transition()
    {
        var root = Registry(Pulled);
        var plan = await Plan(root, "Done", "To Do");
        Assert.IsType<MirrorAction.Drift>(Assert.Single(plan));

        Assert.Equal(1, await JiraMirrorReport.FlagDriftAsync(root, plan, DateTimeOffset.UtcNow, default));
        await JiraMirrorReport.FlagDriftAsync(root, plan, DateTimeOffset.UtcNow.AddMinutes(5), default);

        var rows = File.ReadAllLines(Path.Combine(root, "ledger", "pipeline.jsonl"));
        var row = Assert.Single(rows, r => r.Contains("item_awaiting_human"));
        Assert.Contains("Jira REQ-1 says", row);
        Assert.Contains("In Progress", row);
    }
}
