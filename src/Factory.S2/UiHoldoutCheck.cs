using System.Security.Cryptography;
using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Harness.Registry;
using Factory.Harness.Sandbox;
using Factory.S2.Git;
using Factory.S2.Verify;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;

namespace Factory.S2;

/// What the holdout said per screen: "pass" | "fail" | "could_not_run" | "no_holdout".
public sealed record HoldoutRun(List<VerifyOutcome> Outcomes, Dictionary<string, string> Screens);

/// The holdout: sample data the build never saw, run against the integrated branch in a scratch copy that is thrown away. The build,
/// its briefs, its worktrees and its repo never hold it; the Factory process reads it from the registry, checks it against the hash the
/// freeze recorded, and writes it (and the generated test) into the scratch copy only. Nothing the runner prints leaves the scratch copy:
/// the ledger and the briefs get state names and fixed text.
public sealed class UiHoldoutCheck(ILedger ledger, ISandboxFactory sandboxes, string registryPath, string command)
{
    private ValueTask LogAsync(string runId, string kind, string? outcome = null, string? payload = null, CancellationToken ct = default) =>
        ledger.AppendAsync(new LedgerEvent(runId, "S2", "integration_verify", kind, Outcome: outcome, Payload: payload), ct);

    public async Task<HoldoutRun> RunAsync(string runId, Handover handover, FSharpList<PlannedTask> tasks, GitWorkspace git, TimeSpan timeout, CancellationToken ct)
    {
        var outcomes = new List<VerifyOutcome>();
        var results = new Dictionary<string, string>();
        // screen -> (sample JSON, reason it cannot run)
        var samples = new Dictionary<string, string>();
        var notRun = new Dictionary<string, string>();
        foreach (var screen in handover.Ui)
        {
            if (!FSharpOption<string>.get_IsSome(screen.Holdout))
            {
                results[screen.Screen] = "no_holdout";
                await LogAsync(runId, LedgerKinds.Verify, outcome: "no_holdout",
                    payload: JsonSerializer.Serialize(new { name = "holdout", screen = screen.Screen, outcome = "no_holdout" }), ct: ct);
                continue;
            }
            var path = Path.Combine(registryPath, "ui", "holdout", screen.Screen + ".json");
            if (!File.Exists(path)) { notRun[screen.Screen] = "the holdout for this screen was not found in the registry"; continue; }
            var bytes = await File.ReadAllBytesAsync(path, ct);
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), screen.Holdout.Value, StringComparison.OrdinalIgnoreCase))
            { notRun[screen.Screen] = "the holdout changed since the freeze"; continue; }
            samples[screen.Screen] = System.Text.Encoding.UTF8.GetString(bytes);
        }

        var readings = new Dictionary<string, (string Outcome, string Tail, List<string> States)>();
        foreach (var (name, why) in notRun) readings[name] = ("could_not_run", "could_not_run: " + why, []);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (samples.Count > 0)
        {
            await using var scratch = await git.BaselineCheckoutAsync(runId, ct);
            var tests = Path.Combine(scratch.Path, "ui-contract", "tests");
            var holdoutDir = Path.Combine(scratch.Path, "ui-contract", "holdout");
            Directory.CreateDirectory(tests);
            Directory.CreateDirectory(holdoutDir);
            foreach (var screen in handover.Ui.Where(s => samples.ContainsKey(s.Screen)))
            {
                await File.WriteAllTextAsync(Path.Combine(tests, screen.Screen + ".holdout.test.tsx"), UiHoldoutTest.text(screen.Screen, screen.Component, screen.File), ct);
                await File.WriteAllTextAsync(Path.Combine(holdoutDir, screen.Screen + ".json"), samples[screen.Screen], ct);
            }

            string? report = null, couldNot = null;
            var configured = HasHoldoutProject(scratch.Path);
            if (!configured) couldNot = "the app's Vitest config has no ui-holdout project";
            else
            {
                await using var sandbox = await sandboxes.ForTeamAsync("holdout", scratch.Path, ct);
                var report0 = Path.Combine(scratch.Path, ".vitest", "json", "output.json");
                if (File.Exists(report0)) File.Delete(report0);
                var run = await sandbox.RunAsync(command, scratch.Path, timeout,
                    new Dictionary<string, string> { ["CHROME_PATH"] = UiPreviewRunner.ChromePath, ["CI"] = "1", ["FACTORY_RUN_ID"] = runId }, ct);
                var reported = Factory.Harness.Registry.UiPreview.ReportPath(run.Output) ?? report0;
                if (File.Exists(reported)) report = await File.ReadAllTextAsync(reported, ct);
                else if (run.Output.TrimStart().StartsWith('{')) report = run.Output;
                if (report is null) couldNot = run.TimedOut ? "the holdout run timed out" : $"the holdout run wrote no report (exit {run.ExitCode})";
            }

            foreach (var screen in handover.Ui.Where(s => samples.ContainsKey(s.Screen)))
            {
                if (report is null) { readings[screen.Screen] = ("could_not_run", "could_not_run: " + couldNot, []); continue; }
                var states = ListModule.OfSeq(UiSpecStates.ofSpec(screen.Spec.Text));
                var reading = UiHoldoutReport.read(screen.Screen, states, report);
                var tail = UiHoldoutReport.tail(screen.Screen, reading);
                readings[screen.Screen] = reading.IsHoldoutHeld ? ("pass", tail, [])
                    : reading is HoldoutReading.HoldoutFailedStates f ? ("fail", tail, f.Item.ToList())
                    : ("could_not_run", tail, []);
            }
        }

        var ms = sw.ElapsedMilliseconds;
        var commit = await git.HeadAsync(git.IntegrationPath(runId), ct);
        foreach (var screen in handover.Ui)
        {
            if (!readings.TryGetValue(screen.Screen, out var r)) continue;
            results[screen.Screen] = r.Outcome;
            var (element, blamed) = UiBlame.tasks(handover.Elements, tasks, screen.Component, screen.File);
            var elementId = FSharpOption<string>.get_IsSome(element) ? element.Value : null;
            await LogAsync(runId, LedgerKinds.Verify, outcome: r.Outcome == "pass" ? "passed" : "failed",
                payload: JsonSerializer.Serialize(new { name = "holdout", screen = screen.Screen, outcome = r.Outcome, ms, states_failed = r.States }), ct: ct);
            foreach (var state in r.States)
                foreach (var task in blamed)
                    await ledger.AppendAsync(new LedgerEvent(runId, "S2", "integration_verify", LedgerKinds.HoldoutFailed, TaskId: task.Value, Element: elementId,
                        Outcome: "failed", Payload: JsonSerializer.Serialize(new { screen = screen.Screen, state })), ct);
            if (r.Outcome != "pass")
                outcomes.Add(new VerifyOutcome("holdout:" + screen.Screen, r.Outcome == "fail" ? 1 : 3, ms,
                    r.Outcome == "fail" && blamed.IsEmpty ? r.Tail + "\nno task changes its element or writes " + screen.File : r.Tail));
        }
        return new HoldoutRun(outcomes, results);
    }

    /// The app's own Vitest config must define the project the generated test runs under; without it nothing here has run.
    private static bool HasHoldoutProject(string dir) =>
        Directory.GetFiles(dir, "vitest.config.*", SearchOption.AllDirectories)
            .Where(f => !f.Contains("node_modules"))
            .Any(f => File.ReadAllText(f).Contains("ui-holdout", StringComparison.Ordinal));
}
