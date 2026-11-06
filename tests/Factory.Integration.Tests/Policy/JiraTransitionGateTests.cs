using System.Text.Json;
using Factory.Core.Gate;
using Factory.Harness;
using Factory.Harness.Governance;
using Factory.Integration.Tests.Support;
using Microsoft.FSharp.Collections;
using Xunit;

namespace Factory.Integration.Tests.Policy;

/// "No ReadyForQA transition without a Proof for that subject and commit", through the ACS policy file.
public class JiraTransitionGateTests
{
    private static Proof ProofFor(string subject, string commit) =>
        ((Microsoft.FSharp.Core.FSharpResult<Proof, FSharpList<string>>)
            ProofModule.tryCreate(FSharpList<string>.Empty,
                new ProofBundle(subject, commit, FSharpList<CheckResult>.Empty,
                    ListModule.OfSeq([new Approval("mike", "ci")])))).ResultValue;

    private static (JiraTransitionGate Gate, MemoryLedger Ledger) Make(bool enforce)
    {
        var ledger = new MemoryLedger();
        return (new JiraTransitionGate(AcsToolPolicy.Load(AcsToolPolicy.DefaultDirectory), ledger, enforce), ledger);
    }

    [Fact]
    public async Task Ready_for_qa_is_allowed_with_a_matching_proof()
    {
        var (gate, _) = Make(true);
        var d = await gate.DecideAsync("run1", "T-1", "ReadyForQA", "abc", [ProofFor("T-1", "abc")]);
        Assert.True(d.Allowed, d.Reason);
    }

    [Fact]
    public async Task Ready_for_qa_is_denied_with_no_proof()
    {
        var (gate, _) = Make(true);
        Assert.False((await gate.DecideAsync("run1", "T-1", "ReadyForQA", "abc", [])).Allowed);
    }

    [Fact]
    public async Task Ready_for_qa_is_denied_with_a_proof_for_a_different_commit_or_ticket()
    {
        var (gate, _) = Make(true);
        Assert.False((await gate.DecideAsync("run1", "T-1", "ReadyForQA", "abc", [ProofFor("T-1", "old")])).Allowed);
        Assert.False((await gate.DecideAsync("run1", "T-1", "ReadyForQA", "abc", [ProofFor("T-2", "abc")])).Allowed);
    }

    [Fact]
    public async Task Ready_for_qa_in_a_split_run_needs_every_repo_merged_as_well_as_a_proof()
    {
        var (gate, ledger) = Make(true);
        var proofs = new[] { ProofFor("T-1", "abc") };

        Assert.False((await gate.DecideAsync("run1", "T-1", "ReadyForQA", "abc", proofs, allReposMerged: false)).Allowed);
        Assert.False((await gate.DecideAsync("run1", "T-1", "ReadyForQA", "abc", [], allReposMerged: true)).Allowed);
        Assert.True((await gate.DecideAsync("run1", "T-1", "ReadyForQA", "abc", proofs, allReposMerged: true)).Allowed);

        using var p = JsonDocument.Parse(ledger.Events[0].Payload!);
        Assert.Equal("false", p.RootElement.GetProperty("args").GetProperty("all_repos_merged").GetString());
    }

    [Fact]
    public async Task A_single_repo_run_passes_all_repos_merged_as_true_by_default()
    {
        var (gate, ledger) = Make(true);
        Assert.True((await gate.DecideAsync("run1", "T-1", "ReadyForQA", "abc", [ProofFor("T-1", "abc")])).Allowed);
        using var p = JsonDocument.Parse(ledger.Events[0].Payload!);
        Assert.Equal("true", p.RootElement.GetProperty("args").GetProperty("all_repos_merged").GetString());
    }

    [Fact]
    public async Task The_start_transition_is_always_allowed_and_others_are_denied()
    {
        var (gate, _) = Make(true);
        Assert.True((await gate.DecideAsync("run1", "T-1", "InProgress", "abc", [])).Allowed);
        Assert.False((await gate.DecideAsync("run1", "T-1", "Done", "abc", [ProofFor("T-1", "abc")])).Allowed);
        Assert.False((await gate.DecideAsync("run1", "T-1", "Rejected", "abc", [])).Allowed);
    }

    [Fact]
    public async Task Every_decision_is_logged_with_policy_decision_reason_and_mode()
    {
        var (gate, ledger) = Make(true);
        await gate.DecideAsync("run1", "T-1", "ReadyForQA", "abc", []);

        var e = Assert.Single(ledger.Events);
        Assert.Equal(LedgerKinds.ToolCall, e.Kind);
        Assert.Equal("denied", e.Outcome);
        using var p = JsonDocument.Parse(e.Payload!);
        Assert.Equal("jira.transition", p.RootElement.GetProperty("tool").GetString());
        Assert.Equal("enforce", p.RootElement.GetProperty("mode").GetString());
        Assert.Contains("jira-gate.yaml", p.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Shadow_mode_logs_what_it_would_do_but_never_blocks()
    {
        var (gate, ledger) = Make(false);
        var d = await gate.DecideAsync("run1", "T-1", "ReadyForQA", "abc", []);

        Assert.False(d.Allowed);
        Assert.False(d.Enforced);
        using var p = JsonDocument.Parse(Assert.Single(ledger.Events).Payload!);
        Assert.Equal("shadow", p.RootElement.GetProperty("mode").GetString());
    }
}
