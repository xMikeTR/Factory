using System.Security.Cryptography;
using System.Text;
using Factory.Core;
using Factory.Harness;
using Factory.Harness.Registry;
using Xunit;

namespace Factory.Integration.Tests.Registry;

public class ProceduresTests
{
    private static (string Base, string Repo) Layout()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2-procedures-" + Guid.NewGuid().ToString("N"));
        string bin = Path.Combine(root, "bin"), repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(Path.Combine(bin, "prompts"));
        Directory.CreateDirectory(Path.Combine(bin, "policies", "s2"));
        Directory.CreateDirectory(Path.Combine(repo, ".factory"));
        File.WriteAllText(Path.Combine(bin, "prompts", "implementer.md"), "You implement.");
        File.WriteAllText(Path.Combine(bin, "prompts", "plan.schema.json"), "{}"); // a schema, not a procedure
        File.WriteAllText(Path.Combine(bin, "policies", "s2", "jira-gate.yaml"), "# source: hand-authored\nname: jira-gate\nversion: \"3\"\nrules: []\n");
        File.WriteAllText(Path.Combine(repo, ".factory", "policy.yaml"), "budget: 1000\n");
        return (bin, repo);
    }

    private static string Sha(string text) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void Prompts_tool_and_gate_policies_and_the_target_policy_are_fingerprinted_in_id_order()
    {
        var (bin, repo) = Layout();

        var refs = Procedures.Fingerprint(bin, repo);

        Assert.Equal(
        [
            new ProcedureRef("policy:jira-gate", "3", Sha("# source: hand-authored\nname: jira-gate\nversion: \"3\"\nrules: []\n")),
            new ProcedureRef("prompt:implementer", "none", Sha("You implement.")),
            new ProcedureRef("target:policy", "none", Sha("budget: 1000\n")),
        ], refs);
    }

    [Fact]
    public void A_changed_prompt_changes_its_hash_and_nothing_else()
    {
        var (bin, repo) = Layout();
        var before = Procedures.Fingerprint(bin, repo);

        File.WriteAllText(Path.Combine(bin, "prompts", "implementer.md"), "You implement, carefully.");
        var after = Procedures.Fingerprint(bin, repo);

        Assert.Equal(before.Where(r => r.Id != "prompt:implementer"), after.Where(r => r.Id != "prompt:implementer"));
        Assert.NotEqual(before.Single(r => r.Id == "prompt:implementer").Hash, after.Single(r => r.Id == "prompt:implementer").Hash);
    }

    [Fact]
    public void Missing_directories_and_no_target_policy_give_what_is_there()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2-procedures-" + Guid.NewGuid().ToString("N"));

        Assert.Empty(Procedures.Fingerprint(Path.Combine(root, "bin"), Path.Combine(root, "repo")));
    }

    [Fact]
    public void The_run_started_payload_reads_back_as_the_traces_procedures()
    {
        var (bin, repo) = Layout();
        var refs = Procedures.Fingerprint(bin, repo);
        var run = new List<LedgerEvent>
        {
            new("run-1", "S2", "start", LedgerKinds.RunStarted, Payload: Procedures.RunStartedPayload(refs)),
            new("run-1", "S2", "task", LedgerKinds.TaskEvent, TaskId: "T-001", Outcome: "Failed",
                Payload: Factory.Core.Core.EncodeEvent(TaskEvent.NewAbandon("rejected"))),
        };

        var trace = Assert.Single(TraceReader.Read(run, new Limits(5, 3)));

        Assert.Equal(refs, trace.Procedures);
    }
}
