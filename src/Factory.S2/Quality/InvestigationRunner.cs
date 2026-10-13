using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.S2.Teams;
using Microsoft.Extensions.AI;

namespace Factory.S2.Quality;

/// The read-only investigators: `s2-diagnosis` explains why a frozen spec failed (before a repair run re-runs the
/// blamed task) and `s2-repro` reproduces a reported escape (before anyone writes its failing spec). Neither can
/// write a file; both are paid model calls and run only when asked for.
public static class InvestigationRunner
{
    /// The repair feedback with each task's diagnosis appended to its brief. A task the agent could not diagnose keeps its brief.
    public static async Task<RepairFile> DiagnoseAsync(
        FactoryServices svc, RepairFile repair, string worktree, CancellationToken ct)
    {
        var feedback = new Dictionary<string, string>(repair.Feedback);
        foreach (var (task, brief) in repair.Feedback)
        {
            var finding = await AskAsync(svc, repair.Of, Roles.Diagnosis, "diagnosis", task, worktree, brief, s => s.Diagnosis, ct);
            if (finding is null) continue;
            feedback[task] = brief + "\n\nDiagnosis (from the trace, by a read-only agent; check it before you rely on it):\n" + finding;
            await svc.LogAsync(repair.Of, "repair", LedgerKinds.Diagnosis, taskId: task, outcome: "diagnosed",
                payload: JsonSerializer.Serialize(new { finding }), ct: ct);
        }
        return repair with { Feedback = feedback };
    }

    /// The steps that reproduce `report`, or null when the agent recorded none.
    public static async Task<string?> ReproduceAsync(
        FactoryServices svc, string runId, string report, string worktree, CancellationToken ct)
    {
        var steps = await AskAsync(svc, runId, Roles.Repro, "repro", null, worktree, report, s => s.Reproduction, ct);
        await svc.LogAsync(runId, "intake", LedgerKinds.Reproduction, outcome: steps is null ? "not_reproduced" : "reproduced",
            payload: JsonSerializer.Serialize(new { report, steps }), ct: ct);
        return steps;
    }

    private static async Task<string?> AskAsync(
        FactoryServices svc, string runId, string role, string prompt, string? taskId, string worktree, string message,
        Func<TurnSignals, string?> result, CancellationToken ct)
    {
        var signals = new TurnSignals();
        await using var sandbox = svc.Settings.PlaywrightCli ? await svc.Sandboxes.ForTeamAsync("investigate", worktree, ct) : null;
        var workspace = new ScopedWorkspace(worktree, [], [], [], signals, sandbox);
        var tools = role == Roles.Repro ? workspace.ReproTools() : workspace.DiagnosisTools();
        var agent = svc.Agents.Create(new AgentScope(runId, role, "investigate", taskId), ModelTier.Standard,
            Briefs.Prompt(prompt), tools, new TurnBudget());
        await agent.RunAsync(new ChatMessage(ChatRole.User, message), cancellationToken: ct);
        return result(signals);
    }
}
