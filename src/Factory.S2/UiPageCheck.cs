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

public sealed class UiPageCheck(ILedger ledger, ISandboxFactory sandboxes, string command)
{
    private ValueTask LogAsync(string runId, string kind, string? outcome = null, string? payload = null, CancellationToken ct = default) =>
        ledger.AppendAsync(new LedgerEvent(runId, "S2", "integration_verify", kind, Outcome: outcome, Payload: payload), ct);

    /// The ui: check, once per screen of the handover: the static code check of the page file, then the generated page tests (aria,
    /// pixels, placements, per state) in the runner image. Each failure is a `ui_check_failed` row per blamed task; each screen
    /// gets one `ui_result` on the integration commit. Failures name screen and state, never sample values.
    public async Task<List<VerifyOutcome>> RunAsync(string runId, Handover handover, FSharpList<PlannedTask> tasks, GitWorkspace git, string integration, TimeSpan timeout, CancellationToken ct)
    {
        var outcomes = new List<VerifyOutcome>();
        if (handover.Ui.IsEmpty) return outcomes;
        var commit = await git.HeadAsync(integration, ct);

        // One run for every screen: either it ran and says what failed, or it could not run and says why.
        string? report = null;
        string? couldNot = null;
        var tail = "";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using (var sandbox = await sandboxes.ForTeamAsync("integration", integration, ct))
        {
            if (sandbox.Kind != "docker") couldNot = "page pixels need the runner image: baselines depend on its platform and fonts";
            else
            {
                var report0 = Path.Combine(integration, ".vitest", "json", "output.json");
                if (File.Exists(report0)) File.Delete(report0);
                var run = await sandbox.RunAsync(command, integration, timeout,
                    new Dictionary<string, string> { ["CHROME_PATH"] = UiPreviewRunner.ChromePath, ["CI"] = "1", ["FACTORY_RUN_ID"] = runId }, ct);
                tail = run.Tail(2000);
                var path = Factory.Harness.Registry.UiPreview.ReportPath(run.Output) ?? report0;
                if (File.Exists(path)) report = await File.ReadAllTextAsync(path, ct);
                else if (run.Output.TrimStart().StartsWith('{')) report = run.Output;
                if (report is null) couldNot = (run.TimedOut ? "the page tests timed out" : $"the page tests wrote no report (exit {run.ExitCode})") + ":\n" + tail;
            }
        }

        foreach (var screen in handover.Ui)
        {
            var failures = new List<(string? State, string? Placement, string Kind, string? Detail)>();
            var pagePath = Path.Combine(integration, screen.File);
            if (!File.Exists(pagePath)) failures.Add((null, null, "code", $"the page file {screen.File} does not exist"));
            else
                foreach (var p in UiCodeCheck.check(screen.Spec.Text, ListModule.OfSeq(screen.Components.Select(c => c.Text)), await File.ReadAllTextAsync(pagePath, ct)))
                    failures.Add((null, null, "code", $"{p.Rule}: {p.Detail}"));

            var ranOk = couldNot is null;
            string? notRun = couldNot;
            if (report is not null)
            {
                try
                {
                    var states = UiSpecStates.ofSpec(screen.Spec.Text).Length;
                    foreach (var f in UiReport.failures(screen.Screen, report))
                        failures.Add((f.State, FSharpOption<string>.get_IsSome(f.Placement) ? f.Placement.Value : null, f.Kind, f.Detail));
                    if (failures.All(f => f.Kind == "code") && UiReport.executed(screen.Screen, report) < 3 * states)
                    { ranOk = false; notRun = $"only some of the {3 * states} page tests for '{screen.Screen}' ran:\n" + tail; }
                }
                catch (JsonException) { ranOk = false; notRun = "the page test report could not be read:\n" + tail; }
            }

            var outcome = failures.Count > 0 ? "fail" : ranOk ? "pass" : "could_not_run";
            var (element, blamed) = UiBlame.tasks(handover.Elements, tasks, screen.Component, screen.File);
            var elementId = FSharpOption<string>.get_IsSome(element) ? element.Value : null;
            var blamedIds = blamed.Select(t => t.Value).ToList();
            var lines = failures.Select(f => $"{screen.Screen}{(f.State is null ? "" : " / " + f.State)}: {f.Kind}{(f.Placement is null ? "" : " " + f.Placement)}"
                + (f.Kind == "code" ? " - " + f.Detail : "")).ToList();
            var summary = outcome switch
            {
                "pass" => $"ui:{screen.Screen} passed",
                "fail" => string.Join("\n", lines) + (blamedIds.Count == 0 ? "\nno task changes its element or writes " + screen.File : "\ntasks: " + string.Join(", ", blamedIds)),
                _ => "could_not_run: " + notRun,
            };
            foreach (var f in failures)
                foreach (var task in blamedIds)
                    await ledger.AppendAsync(new LedgerEvent(runId, "S2", "integration_verify", LedgerKinds.UiCheckFailed, TaskId: task, Element: elementId, Outcome: "failed",
                        Payload: JsonSerializer.Serialize(new { screen = screen.Screen, state = f.State, placement = f.Placement, kind = f.Kind, detail = f.Kind == "code" ? f.Detail : null, commit })), ct);
            await LogAsync(runId, LedgerKinds.Verify, outcome: outcome == "pass" ? "passed" : "failed",
                payload: JsonSerializer.Serialize(new { name = "ui:" + screen.Screen, ExitCode = outcome == "pass" ? 0 : outcome == "fail" ? 1 : 3, kinds = failures.Select(f => f.Kind).Distinct() }), ct: ct);
            await LogAsync(runId, LedgerKinds.UiResult, outcome: outcome,
                payload: JsonSerializer.Serialize(new { screen = screen.Screen, commit, outcome, item = FSharpOption<string>.get_IsSome(handover.WorkItem) ? handover.WorkItem.Value : null }), ct: ct);
            outcomes.Add(new VerifyOutcome("ui:" + screen.Screen, outcome == "pass" ? 0 : outcome == "fail" ? 1 : 3, sw.ElapsedMilliseconds, summary));
        }
        return outcomes;
    }
}
