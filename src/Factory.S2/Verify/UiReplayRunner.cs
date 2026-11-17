using System.Text.Json;
using System.Text.RegularExpressions;
using Factory.Core;
using Factory.Harness;
using Factory.Harness.Registry;
using Factory.Harness.Sandbox;
using Factory.S2.Git;

namespace Factory.S2.Verify;

/// `factory-s2 replay --registry <dir> --scenario <id> [--repo .]`: the screen's page rendered in the runner image against the HAR
/// designc recorded for that scenario (`parity/har/<id>-legacy.har`), served as MSW handlers (docs/ui-contract-verify.md §7).
public sealed record UiReplayRequest(string Registry, string Scenario, string Repo, string Command, TimeSpan? Timeout = null);

/// Exit: 0 every request was served from the HAR, 1 the page made a request the HAR lacks, 3 could not run, 2 error.
public sealed record UiReplayResult(int ExitCode, string Message);

/// Runs in a scratch worktree of the repo's HEAD (the person's checkout is never written) and writes no ledger row: it is a tool for
/// the person, not a gate. The HAR holds page data, so it lives only in the scratch copy and nothing of it is printed.
public sealed class UiReplayRunner(ISandboxFactory sandboxes)
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    public async Task<UiReplayResult> RunAsync(UiReplayRequest r, CancellationToken ct)
    {
        if (!Regex.IsMatch(r.Scenario, "^[A-Za-z0-9][A-Za-z0-9._-]*$")) return new(2, $"'{r.Scenario}' is not a scenario id");
        var registry = Path.GetFullPath(r.Registry);
        var repo = Path.GetFullPath(r.Repo);
        var scenario = Path.Combine(registry, "parity", "scenarios", r.Scenario + ".yaml");
        var har = Path.Combine(registry, "parity", "har", r.Scenario + "-legacy.har");
        if (!File.Exists(scenario)) return new(2, $"no scenario '{r.Scenario}': {scenario} does not exist");
        if (!File.Exists(har)) return new(2, $"no HAR for '{r.Scenario}': {har} does not exist (record legacy with --har)");
        var screen = Regex.Match(await File.ReadAllTextAsync(scenario, ct), @"^screen:\s*['""]?([a-z0-9-]+)['""]?\s*$", RegexOptions.Multiline);
        if (!screen.Success) return new(2, $"scenario '{r.Scenario}' names no screen");
        var specPath = Path.Combine(registry, "ui", "specs", screen.Groups[1].Value + ".json");
        if (!File.Exists(specPath)) return new(2, $"no spec for '{screen.Groups[1].Value}': {specPath} does not exist");

        string component, file;
        using (var spec = JsonDocument.Parse(await File.ReadAllTextAsync(specPath, ct)))
        {
            component = spec.RootElement.TryGetProperty("component", out var c) ? c.GetString() ?? "" : "";
            file = spec.RootElement.TryGetProperty("file", out var f) ? f.GetString() ?? "" : "";
        }
        if (component == "" || file == "") return new(2, $"the spec for '{screen.Groups[1].Value}' names no component or file");

        var groups = HarReplay.groups(await File.ReadAllTextAsync(har, ct));
        if (groups.IsError) return new(2, groups.ErrorValue);

        try
        {
            if ((await new GitWorkspace(repo, Path.Combine(repo, ".factory")).ChangedPathsAsync(repo, ct)).Count > 0)
                return new(2, "the repo has uncommitted changes: a replay runs a commit, so commit first");
        }
        catch (Exception e) when (e is InvalidOperationException) { return new(2, $"{repo} is not a usable git repository: {e.Message}"); }

        var scratch = Path.Combine(Path.GetTempPath(), "s2-replay-" + Guid.NewGuid().ToString("N"));
        var add = await ProcessRunner.RunAsync("git", ["-C", repo, "worktree", "add", "--detach", scratch, "HEAD"], repo, GitTimeout, ct: ct);
        if (!add.Succeeded) return new(2, "could not create the scratch worktree: " + add.Tail(1000));
        try { return await ReplayAsync(r, screen.Groups[1].Value, component, file, groups.ResultValue, scratch, ct); }
        finally
        {
            await ProcessRunner.RunAsync("git", ["-C", repo, "worktree", "remove", "--force", scratch], repo, GitTimeout);
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
            await ProcessRunner.RunAsync("git", ["-C", repo, "worktree", "prune"], repo, GitTimeout);
        }
    }

    private async Task<UiReplayResult> ReplayAsync(UiReplayRequest r, string screen, string component, string file,
        Microsoft.FSharp.Collections.FSharpList<ReplayGroup> groups, string worktree, CancellationToken ct)
    {
        var contract = Path.Combine(worktree, "ui-contract");
        if (!File.Exists(Path.Combine(contract, "preview", "vitest-ui.tsx")))
            return new(2, "the repo has no ui-contract/preview/vitest-ui.tsx (the kit's helper)");
        Directory.CreateDirectory(Path.Combine(contract, "replay"));
        Directory.CreateDirectory(Path.Combine(contract, "tests"));
        await File.WriteAllTextAsync(Path.Combine(contract, "replay", screen + ".json"), HarReplay.toJson(groups), ct);
        // `.page.test.tsx` so the kit's own ui-page project picks it up: the Factory writes no Vitest config.
        var test = $"ui-contract/tests/{screen}.replay.page.test.tsx";
        await File.WriteAllTextAsync(Path.Combine(worktree, test), HarReplay.testText(screen, component, file), ct);

        await using var sandbox = await sandboxes.ForTeamAsync("replay-" + screen, worktree, ct);
        if (sandbox.Kind != "docker") return new(3, "could_not_run: a replay needs the runner image");
        if (sandbox.RestoreRefusal is { Count: > 0 } refusal)
            return new(2, "the restore window was refused:\n" + string.Join("\n", refusal.Select(x => "  - " + x)));
        if (sandbox is DockerSandbox { Restore: { Succeeded: false } failed })
            return new(2, "restore failed:\n" + failed.Tail(2000));

        var command = r.Command.Replace(" --reporter=html", "") + " " + test;
        var run = await sandbox.RunAsync(command, worktree, r.Timeout ?? DefaultTimeout,
            new Dictionary<string, string> { ["CHROME_PATH"] = UiPreviewRunner.ChromePath, ["CI"] = "1" }, ct);
        if (run.ExitCode == 0 && !run.TimedOut) return new(0, $"replay of '{screen}' served every request from the HAR");
        var plain = Regex.Replace(run.Output, "\u001b\\[[0-9;]*m", "");
        var unserved = Regex.Match(plain, Regex.Escape(HarReplay.UnservedMarker) + @"([^\r\n]*?)(?::\s+expected\b[^\r\n]*)?\r?$", RegexOptions.Multiline);
        if (!run.TimedOut && unserved.Success)
            return new(1, $"replay of '{screen}': the page made request(s) the HAR does not hold:{unserved.Groups[1].Value.TrimEnd()}");
        return new(3, $"could_not_run: the replay test did not finish (exit {run.ExitCode}{(run.TimedOut ? ", timed out" : "")}):\n" + run.Tail(3000));
    }
}
