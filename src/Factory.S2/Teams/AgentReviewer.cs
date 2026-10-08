using System.Text;
using Factory.Core;
using Factory.Harness;

namespace Factory.S2.Teams;

/// Reviews with a model of the reviewer pool (Roles.Reviewer), read-only. Whatever the model does
/// short of calling submit_review is a malformed review, never an approval.
public sealed class AgentReviewer(FactoryAgents agents, Independence independence, IReadOnlyList<string> commandAllowList) : IReviewer
{
    public Independence Independence { get; } = independence;

    public async Task<ReviewVerdict> ReviewAsync(ReviewRequest request, CancellationToken ct)
    {
        var signals = new TurnSignals();
        var workspace = new ScopedWorkspace(request.Worktree, [], [], commandAllowList, signals);
        var agent = agents.Create(new AgentScope(request.RunId, Roles.Reviewer, request.TeamId, request.TaskId), ModelTier.Standard,
            Briefs.Prompt("reviewer"), workspace.ReviewerTools(), new TurnBudget());
        await agent.RunAsync(Message(request), cancellationToken: ct);
        return signals.Review is { } findings
            ? new ReviewVerdict.Submitted(findings)
            : new ReviewVerdict.Malformed("the reviewer ended its turn without calling submit_review");
    }

    private static string Message(ReviewRequest r)
    {
        var sb = new StringBuilder(r.Brief).AppendLine().AppendLine();
        sb.AppendLine($"## Review round {r.Round}");
        if (r.Open.Count > 0)
        {
            sb.AppendLine("Open findings from the previous round (still blocking until the diff fixes them):");
            foreach (var f in r.Open) sb.AppendLine($"- {f.Category} {f.File}:{f.Line}: {f.Message}");
            sb.AppendLine();
        }
        sb.AppendLine("## The diff to review").AppendLine("```diff").AppendLine(r.Diff.TrimEnd()).AppendLine("```");
        return sb.ToString();
    }
}
