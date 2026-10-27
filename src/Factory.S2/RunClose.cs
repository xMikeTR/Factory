using System.Text.Json;
using Factory.Harness;

namespace Factory.S2;

/// `factory-s2 close`: the factory never merges in review mode, so a run ends `ready_for_review` and a
/// person merges its PR. This confirms that merge and records it (a Merge row, stage "close"), so the
/// registry closes the handover on the decision checks integration verify already ran.
public static class RunClose
{
    /// Null when recorded, else why nothing was recorded. `merged` says how the merge shows ("pr <url>",
    /// "<branch> in <base>"), or null when it cannot be seen; then only a person's `by` records it.
    public static async Task<string?> RecordAsync(ILedger ledger, string runId, Func<CancellationToken, Task<string?>> merged, string? by, bool canState, CancellationToken ct)
    {
        LedgerEvent? finished = null;
        var closed = false;
        await foreach (var e in ledger.ReadAsync(runId, ct))
        {
            if (e.Kind == LedgerKinds.Run && e.TaskId is null) (finished, closed) = (e, false);
            else if (e is { Kind: LedgerKinds.Merge, Stage: "close" }) closed = true;
        }
        if (finished?.Outcome != "ready_for_review") return $"run {runId} is not waiting on a merge (it ended {finished?.Outcome ?? "no attempt yet"})";
        if (closed) return $"run {runId} is already closed";

        var how = await merged(ct);
        if (how is null)
        {
            if (!canState) return "its pull request is not merged yet";
            if (string.IsNullOrWhiteSpace(by)) return "the branch is not in the base branch; if you squash-merged it, say so with --by <you>";
            how = $"stated by {by.Trim()}";
        }
        await ledger.AppendAsync(new LedgerEvent(runId, "S2", "close", LedgerKinds.Merge, Outcome: "merged",
            Payload: JsonSerializer.Serialize(new { how, by = string.IsNullOrWhiteSpace(by) ? null : by.Trim() })), ct);
        return null;
    }

    /// Without a PR host: the integration branch is merged when it is an ancestor of the base (a merge, not a squash).
    public static async Task<string?> BranchMergedAsync(string repoPath, string branch, string baseBranch, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync("git", ["merge-base", "--is-ancestor", branch, baseBranch], repoPath, TimeSpan.FromMinutes(1), ct: ct);
        return r.Succeeded ? $"{branch} in {baseBranch}" : null;
    }
}
