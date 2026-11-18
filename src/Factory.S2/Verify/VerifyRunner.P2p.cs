using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.S2.Git;
using Microsoft.FSharp.Collections;

namespace Factory.S2.Verify;

public sealed partial class VerifyRunner
{
    /// FACTORY_P2P_AFFECTED=off turns the affected-only part of the per-task P2P check off (default on).
    public bool P2pAffected { get; set; } = !string.Equals(Environment.GetEnvironmentVariable("FACTORY_P2P_AFFECTED"), "off", StringComparison.OrdinalIgnoreCase);

    private static FSharpList<T> FList<T>(IEnumerable<T> items) => ListModule.OfSeq(items);

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// The per-task pass-to-pass check on the tests the handover names for the task (`named.PassToPass`) plus the tests its change affects
    /// (dotnet-affected for C#, `vitest related` for TS). Exit codes only, no JUnit, so it works where the full-suite regression check is off.
    /// A command that already fails on the integration branch (baseline) is logged `baseline_red` and never blocks. Returns a failing outcome
    /// when a command that was green on the baseline fails now, which joins the green outcomes so the task is not green; null otherwise.
    /// Rows (kind verify): stage `p2p_baseline` {command, sha, exit}, once per command, reused; stage `p2p`, outcome
    /// passed | failed | baseline_red | timed_out | none, payload {command, tests, affected, exit, baseline_exit, attempt}.
    public async Task<VerifyOutcome?> P2pAsync(
        string runId, string teamId, PlannedTask task, int attempt, TaskTests? named, GitWorkspace workspace, string worktree, CancellationToken ct)
    {
        var tests = (named is null ? [] : named.PassToPass.ToList()).Where(t => t.Command?.Value is not null).ToList();
        var commands = tests.Select(t => t.Command!.Value).Distinct().ToList();
        (AffectedTests? affected, string? reason, List<string> extra) = await AffectedAsync(runId, teamId, workspace, worktree, tests.Select(t => t.Path).ToHashSet(), ct);
        foreach (var c in extra) if (!commands.Contains(c)) commands.Add(c);
        var paths = tests.Select(t => t.Path).Concat(affected?.Dotnet ?? []).Concat(affected?.Vitest ?? []).ToList();

        async Task Row(string outcome, string? command, int? exit, int? baselineExit, object? redCommands = null) =>
            await ledger.AppendAsync(new LedgerEvent(runId, "S2", "p2p", LedgerKinds.Verify, TaskId: task.Id.Value, TeamId: teamId, Outcome: outcome,
                Payload: JsonSerializer.Serialize(new
                {
                    command, tests = paths, affected = affected is null ? null : new { dotnet = affected.Dotnet, vitest = affected.Vitest },
                    reason, exit, baseline_exit = baselineExit, attempt, baseline_red = redCommands
                })), ct);

        if (commands.Count == 0) { await Row("none", null, null, null); return null; }

        var baselines = await BaselinesAsync(runId, teamId, commands, workspace, ct);
        var green = commands.Where(c => baselines[c] == 0).ToList();
        var red = commands.Where(c => baselines[c] != 0).ToList();
        if (green.Count == 0)
        {
            await Row("baseline_red", TestPlan.shellLine(FList(commands)), null, baselines[commands[0]], red);
            return null;
        }

        var line = TestPlan.shellLine(FList(green));
        await using var sandbox = await sandboxes.ForTeamAsync(teamId, worktree, ct);
        var r = await sandbox.RunAsync(line, worktree, SuiteTimeout, new Dictionary<string, string> { ["FACTORY_RUN_ID"] = runId }, ct);
        var outcome = r.TimedOut ? "timed_out" : r.ExitCode == 0 ? "passed" : "failed";
        await Row(outcome, line, r.TimedOut ? 124 : r.ExitCode, 0, red.Count == 0 ? null : red);
        if (outcome == "passed") return null;
        if (outcome == "timed_out") return ToOutcome("p2p", r);
        return new VerifyOutcome("p2p", 1, (long)r.Duration.TotalMilliseconds,
            "These tests passed before this task and fail now:\n" + line + "\n" + r.Tail(2000));
    }

    /// Exit code of each command on the integration branch, run once per command text (sha256) and read back from the ledger after that.
    private async Task<Dictionary<string, int>> BaselinesAsync(string runId, string teamId, List<string> commands, GitWorkspace workspace, CancellationToken ct)
    {
        var known = new Dictionary<string, int>();
        await foreach (var e in ledger.ReadAsync(runId, ct))
            if (e is { Kind: LedgerKinds.Verify, Stage: "p2p_baseline", Payload: not null })
            {
                using var doc = JsonDocument.Parse(e.Payload);
                known[doc.RootElement.GetProperty("sha").GetString()!] = doc.RootElement.GetProperty("exit").GetInt32();
            }
        var missing = commands.Where(c => !known.ContainsKey(Sha(c))).ToList();
        if (missing.Count > 0)
        {
            await using var checkout = await workspace.BaselineCheckoutAsync(runId, ct);
            await using var sandbox = await sandboxes.ForTeamAsync($"{teamId}-p2p-baseline", checkout.Path, ct);
            foreach (var c in missing)
            {
                var r = await sandbox.RunAsync(c, checkout.Path, SuiteTimeout, new Dictionary<string, string> { ["FACTORY_RUN_ID"] = runId }, ct);
                var exit = r.TimedOut ? 124 : r.ExitCode;
                known[Sha(c)] = exit;
                await ledger.AppendAsync(new LedgerEvent(runId, "S2", "p2p_baseline", LedgerKinds.Verify, TeamId: teamId,
                    Outcome: exit == 0 ? "recorded" : "baseline_red", Payload: JsonSerializer.Serialize(new { command = c, sha = Sha(c), exit })), ct);
            }
        }
        return commands.ToDictionary(c => c, c => known[Sha(c)]);
    }

    private sealed record AffectedTests(List<string> Dotnet, List<string> Vitest);

    /// The tests the task's change reaches: (affected, reason when it could not be had, extra commands to run). Never throws on a tool problem.
    private async Task<(AffectedTests? Affected, string? Reason, List<string> Commands)> AffectedAsync(
        string runId, string teamId, GitWorkspace workspace, string worktree, HashSet<string> alreadyNamed, CancellationToken ct)
    {
        if (!P2pAffected) return (null, "FACTORY_P2P_AFFECTED=off", []);
        var changed = (await workspace.TaskPathsAsync(worktree, runId, ct)).Select(p => p.Replace('\\', '/')).ToList();
        var files = (await workspace.RepoFilesAsync(worktree, ct)).Select(p => p.Replace('\\', '/')).ToList();
        var dotnet = new List<string>();
        var vitest = new List<string>();
        var commands = new List<string>();
        string? reason = null;

        var projects = files.Where(f => f.EndsWith(".csproj", StringComparison.Ordinal) || f.EndsWith(".fsproj", StringComparison.Ordinal)).ToList();
        var owning = Affected.owningProjects(FList(projects), FList(changed));
        if (!owning.IsEmpty)
        {
            await using var sandbox = await sandboxes.ForTeamAsync($"{teamId}-p2p-affected", worktree, ct);
            var r = await sandbox.RunAsync(Affected.dotnetCommand(owning), worktree, TimeSpan.FromMinutes(5), new Dictionary<string, string>(), ct);
            var parsed = Affected.parseDotnet(r.TimedOut ? 124 : r.ExitCode, r.Output);
            if (parsed.IsError) reason = r.ExitCode == 127 ? "dotnet-affected is not installed" : parsed.ErrorValue;
            else
                foreach (var (path, command) in Affected.dotnetTests(FList((await ReadProjectsAsync(parsed.ResultValue, worktree, ct)).Select(t => Tuple.Create(t.Item1, t.Item2)))))
                    if (!alreadyNamed.Contains(path)) { dotnet.Add(path); commands.Add(command); }
        }

        var packages = new List<Tuple<string, string>>();
        foreach (var p in files.Where(f => f == "package.json" || f.EndsWith("/package.json", StringComparison.Ordinal)))
            packages.Add(Tuple.Create(p, await File.ReadAllTextAsync(Path.Combine(worktree, p), ct)));
        foreach (var (dir, relatedFiles, command) in Affected.vitestRelated(FList(packages), FList(changed)))
        {
            vitest.AddRange(relatedFiles.Select(f => dir.Length == 0 ? f : $"{dir}/{f}"));
            commands.Add(command);
        }
        return (reason is null ? new AffectedTests(dotnet, vitest) : null, reason, commands);
    }

    /// (repo-relative path, text) of each affected project dotnet-affected printed.
    private static async Task<List<(string, string)>> ReadProjectsAsync(IEnumerable<string> absolute, string worktree, CancellationToken ct)
    {
        var result = new List<(string, string)>();
        foreach (var path in absolute)
        {
            if (Affected.relativeTo(worktree, path)?.Value is not { } relative || !File.Exists(Path.Combine(worktree, relative))) continue;
            result.Add((relative, await File.ReadAllTextAsync(Path.Combine(worktree, relative), ct)));
        }
        return result;
    }
}
