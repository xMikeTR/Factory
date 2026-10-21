using System.Text.Json;
using Factory.Core.Gate;

namespace Factory.Harness.Governance;

public sealed record GateDecision(bool Allowed, string Reason, bool Enforced);

/// The `jira.transition` policy (policies/s2/jira-gate.yaml) in front of a transition. The one fact
/// the policy cannot compute itself, whether a Proof covers this ticket at the pushed commit, is
/// computed here and handed in as an arg. Every evaluation is ledgered as a tool_call row.
/// Shadow mode (enforce: false) evaluates and logs but `Enforced` is false, so the caller proceeds.
public sealed class JiraTransitionGate(AcsToolPolicy policy, ILedger ledger, bool enforce)
{
    public const string Role = "s2-jira-gate";
    public const string Tool = "jira.transition";

    public async ValueTask<GateDecision> DecideAsync(
        string runId, string ticket, string to, string pushedCommit, IEnumerable<Proof> proofs, CancellationToken ct = default)
    {
        var covered = proofs.Any(p => ProofModule.covers(ticket, pushedCommit, p));
        var args = new Dictionary<string, object?>
        {
            ["ticket"] = ticket,
            ["to"] = to,
            ["commit"] = pushedCommit,
            ["proof_covers_commit"] = covered ? "true" : "false",
        };
        var d = policy.Evaluate(Role, Tool, args);

        await ledger.AppendAsync(new LedgerEvent(runId, "S2", Role, LedgerKinds.ToolCall,
            Outcome: d.Allowed ? "allowed" : "denied",
            Payload: JsonSerializer.Serialize(new
            {
                tool = Tool,
                args = args.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString()),
                reason = d.Reason,
                mode = enforce ? "enforce" : "shadow",
            })), ct);

        return new GateDecision(d.Allowed, d.Reason, Enforced: enforce);
    }
}
