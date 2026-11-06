using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.S2.Git;
using Factory.S2.Teams;
using Factory.S2.Verify;

namespace Factory.S2;

/// `factory-s2 close`: the factory never merges in review mode, so a run ends `ready_for_review` and a
/// person merges its PR. This confirms that merge, runs the integration suite again on the merged commit
/// (the base may have moved since integration verify), and only when it passes records the merge (a Merge
/// row, stage "close"), so the registry closes the handover. A failing suite is logged and the handover
/// stays open: abandoning it is a person's call.
public static class RunClose
{
    /// What the suite on the merged commit found: the commit it ran on, and the failures (null when it passed).
    public sealed record MergedCheck(string Commit, string? Failures);

    /// Null when recorded, else why nothing was recorded. `merged` says how the merge shows ("pr <url>",
    /// "<branch> in <base>"), or null when it cannot be seen; then only a person's `by` records it.
    public static async Task<string?> RecordAsync(ILedger ledger, string runId, Func<CancellationToken, Task<string?>> merged,
        Func<CancellationToken, Task<MergedCheck>> checkMerged, string? by, bool canState, CancellationToken ct,
        Func<string>? notMerged = null)
    {
        LedgerEvent? finished = null;
        var closed = false;
        await foreach (var e in ledger.ReadAsync(runId, ct))
        {
            if (e.Kind == LedgerKinds.Run && e.TaskId is null) (finished, closed) = (e, false);
            else if (e is { Kind: LedgerKinds.Merge, Stage: "close", Outcome: "merged" }) closed = true;
        }
        if (finished?.Outcome != "ready_for_review") return $"run {runId} is not waiting on a merge (it ended {finished?.Outcome ?? "no attempt yet"})";
        if (closed) return $"run {runId} is already closed";

        var how = await merged(ct);
        if (how is null)
        {
            if (!canState) return notMerged?.Invoke() ?? "its pull request is not merged yet";
            if (string.IsNullOrWhiteSpace(by)) return "the branch is not in the base branch; if you squash-merged it, say so with --by <you>";
            how = $"stated by {by.Trim()}";
        }

        var check = await checkMerged(ct);
        if (check.Failures is not null)
        {
            await ledger.AppendAsync(new LedgerEvent(runId, "S2", "close", LedgerKinds.Merge, Outcome: "merged_failing",
                Payload: JsonSerializer.Serialize(new { how, commit = check.Commit, failures = check.Failures })), ct);
            return $"the merged code ({check.Commit}) fails its integration suite; the handover stays open. Fix the base branch, then run close again.\n{check.Failures}";
        }
        await ledger.AppendAsync(new LedgerEvent(runId, "S2", "close", LedgerKinds.Merge, Outcome: "merged",
            Payload: JsonSerializer.Serialize(new { how, commit = check.Commit, by = string.IsNullOrWhiteSpace(by) ? null : by.Trim() })), ct);
        return null;
    }

    /// A run whose merge is recorded is complete: its worktrees and local branches go. True when it was cleaned.
    public static Task<bool> CleanUpAsync(ILedger ledger, GitWorkspace git, string runId, CancellationToken ct) =>
        CleanUpAsync(ledger, [git], runId, ct);

    /// A split run is closed only once every repo's PR is merged (see SplitMerged), so its repos are cleaned together or not at all.
    public static async Task<bool> CleanUpAsync(ILedger ledger, IEnumerable<GitWorkspace> gits, string runId, CancellationToken ct)
    {
        var closed = false;
        await foreach (var e in ledger.ReadAsync(runId, ct))
            if (e is { Kind: LedgerKinds.Merge, Stage: "close", Outcome: "merged" }) closed = true;
        if (closed) foreach (var git in gits) await git.RemoveRunAsync(runId, ct);
        return closed;
    }

    /// One repo of a split run and how to see that its PR is merged (null: not merged).
    public sealed record RepoMerge(string Repo, Func<CancellationToken, Task<string?>> Merged);

    /// A split run is merged only when every repo's PR is: one merged is not complete.
    public sealed class SplitMerged(IReadOnlyList<RepoMerge> repos)
    {
        private IReadOnlyList<string> _open = [];

        /// "Api: pr <url>; Ui: pr <url>" when all are merged, else null.
        public async Task<string?> AllAsync(CancellationToken ct)
        {
            var seen = new List<(string Repo, string? How)>();
            foreach (var r in repos) seen.Add((r.Repo, await r.Merged(ct)));
            _open = [.. seen.Where(x => x.How is null).Select(x => x.Repo)];
            return _open.Count == 0 ? string.Join("; ", seen.Select(x => $"{x.Repo}: {x.How}")) : null;
        }

        public string NotMerged() => $"not every pull request is merged yet: {string.Join(", ", _open)}";
    }

    /// Without a PR host: the integration branch is merged when it is an ancestor of the base (a merge, not a squash).
    public static async Task<string?> BranchMergedAsync(string repoPath, string branch, string baseBranch, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync("git", ["merge-base", "--is-ancestor", branch, baseBranch], repoPath, TimeSpan.FromMinutes(1), ct: ct);
        return r.Succeeded ? $"{branch} in {baseBranch}" : null;
    }

    /// The integration suite, PObserve, the frozen acceptance specs and the token lint, as integration verify runs
    /// them, on a throwaway checkout of `commitish`. Failures blame no task: after a merge they are the base's too.
    public static async Task<MergedCheck> CheckMergedAsync(FactoryServices svc, string runId, string commitish, CancellationToken ct)
    {
        var s = svc.Settings;
        var timeout = TimeSpan.FromMinutes(30);
        await using var checkout = await svc.Git.BaselineCheckoutAsync(runId, ct, commitish);
        var outcomes = new List<VerifyOutcome>();
        if (SuiteDiscovery.Plan(s.IntegrationTestCommand, checkout.Path) is SuitePlan.Command suite)
            outcomes.Add(await svc.Verify.RunCommandAsync(runId, "integration tests", suite.Item, checkout.Path, timeout, ct, stage: "close"));
        if (s.PObserveCommand is { } pobserve)
            outcomes.Add(await svc.Verify.RunCommandAsync(runId, "PObserve", pobserve, checkout.Path, timeout, ct, stage: "close"));
        var handover = svc.LoadHandover(runId);
        if (!handover.AcceptanceSpecs.IsEmpty)
            outcomes.Add((await svc.Verify.AcceptanceAsync(runId, s.AcceptanceCommand, checkout.Path,
                handover.AcceptanceSpecs.Select(a => a.Path).ToList(), ct, stage: "close")).Outcome);
        if (s.TokenLintCommand is { } lint && Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(handover.DesignTokens))
            outcomes.Add((await svc.Verify.TokenLintAsync(runId, lint, checkout.Path, ct, stage: "close")).Outcome);
        var failed = outcomes.Where(o => o.ExitCode != 0).ToList();
        return new MergedCheck(await svc.Git.HeadAsync(checkout.Path, ct), failed.Count == 0 ? null : Briefs.Failures(failed));
    }

    /// A split run's suites on each repo's merged base, each repo's own `integration.testCommand` as in integration verify.
    /// (Acceptance specs, token lint and PObserve are not re-run here: they ran in the Ui repo before the PRs were opened.)
    public static async Task<MergedCheck> CheckMergedSplitAsync(FactoryServices svc, string runId, bool onHost, CancellationToken ct)
    {
        var outcomes = new List<VerifyOutcome>();
        var commits = new List<string>();
        foreach (var repo in SplitMerge.Order(svc.RepoGits.Keys))
        {
            var git = svc.RepoGits[repo];
            if (onHost) await git.FetchAsync(svc.Settings.BaseBranch, ct);
            await using var checkout = await git.BaselineCheckoutAsync(runId, ct, onHost ? $"origin/{svc.Settings.BaseBranch}" : svc.Settings.BaseBranch);
            var command = Factory.Harness.Policy.IntegrationPolicyFile.TestCommand(git.RepoPath) ?? Environment.GetEnvironmentVariable("FACTORY_INTEGRATION_TEST_CMD");
            if (SuiteDiscovery.Plan(command, checkout.Path) is SuitePlan.Command suite)
                outcomes.Add(await svc.Verify.RunCommandAsync(runId, $"integration tests ({repo})", suite.Item, checkout.Path, TimeSpan.FromMinutes(30), ct, stage: "close"));
            commits.Add($"{repo}@{await git.HeadAsync(checkout.Path, ct)}");
        }
        var failed = outcomes.Where(o => o.ExitCode != 0).ToList();
        return new MergedCheck(string.Join(" ", commits), failed.Count == 0 ? null : Briefs.Failures(failed));
    }
}
