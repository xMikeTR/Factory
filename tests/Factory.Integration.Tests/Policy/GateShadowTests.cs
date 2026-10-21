using Factory.Harness;
using Factory.Harness.Governance;
using Factory.Integration.Tests.Support;
using Xunit;

namespace Factory.Integration.Tests.Policy;

public class GateShadowTests
{
    private const string Cases = """
        { "required": ["acs", "mutation"],
          "cases": [
            { "ticket": "T-1", "to": "ReadyForQA", "commit": "abc", "kit": "allow",
              "bundle": { "subject": "T-1", "commit": "abc",
                          "checks": [ {"name":"acs","passed":true}, {"name":"mutation","passed":true} ],
                          "approvals": [ {"by":"mike","source":"ci"} ] } },
            { "ticket": "T-2", "to": "ReadyForQA", "commit": "def", "kit": "allow",
              "bundle": { "subject": "T-2", "commit": "def",
                          "checks": [ {"name":"acs","passed":true} ],
                          "approvals": [ {"by":"mike","source":"ci"} ] } },
            { "ticket": "T-3", "to": "ReadyForQA", "commit": "ghi", "kit": "deny" },
            { "ticket": "T-4", "to": "Done", "commit": "jkl", "kit": "deny" }
          ] }
        """;

    [Fact]
    public async Task It_compares_the_gate_with_the_kit_case_by_case_and_logs_in_shadow_mode()
    {
        var ledger = new MemoryLedger();
        var gate = new JiraTransitionGate(AcsToolPolicy.Load(AcsToolPolicy.DefaultDirectory), ledger, enforce: false);

        var rows = await GateShadow.RunAsync(gate, "shadow1", Cases);

        Assert.Equal([true, false, true, true], rows.Select(r => r.Agrees));
        Assert.Equal("allow", rows[0].Gate);
        Assert.Equal("deny", rows[1].Gate);
        Assert.Contains("mutation", rows[1].Note);   // why no Proof could be made
        Assert.Equal(4, ledger.Events.Count);
        Assert.All(ledger.Events, e => Assert.Contains("\"mode\":\"shadow\"", e.Payload));
    }

    [Fact]
    public void The_report_lists_disagreements_first()
    {
        var text = GateShadow.Render([
            new("T-1", "ReadyForQA", "abc", "allow", "allow", true, ""),
            new("T-2", "ReadyForQA", "def", "allow", "deny", false, "required check 'mutation' is missing")]);

        Assert.StartsWith("1 of 2 agree", text);
        Assert.True(text.IndexOf("T-2", StringComparison.Ordinal) < text.IndexOf("T-1", StringComparison.Ordinal));
    }
}
