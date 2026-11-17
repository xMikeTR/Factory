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

public sealed class UiPageCheck(ILedger ledger, ISandboxFactory sandboxes, string command, bool writesResult = true, string? runDir = null, string? registry = null)
{
    /// The default reporters: `default` prints each failure's diff and step timeline, `json` writes the report the check reads,
    /// `html` writes the report folder (`UiPreview.HtmlReportDir`) kept as evidence.
    public const string DefaultCommand = "npx vitest run --project ui-page --reporter=default --reporter=json --reporter=html";

    /// The same command for runs that only need the JSON report (mutants judge dozens of pages): no terminal report, no HTML.
    public static string JsonOnly(string command) => command.Replace(" --reporter=default", "").Replace(" --reporter=html", "");

    private const string EvidenceRoot = "evidence/ui";

    private ValueTask LogAsync(string runId, string kind, string? outcome = null, string? payload = null, CancellationToken ct = default) =>
        ledger.AppendAsync(new LedgerEvent(runId, "S2", "integration_verify", kind, Outcome: outcome, Payload: payload), ct);

    /// The `ui_result` of a screen on the integration commit. The executor writes it itself (writesResult: false) once the holdout has spoken.
    public ValueTask WriteResultAsync(string runId, string screen, string commit, string outcome, Handover handover, CancellationToken ct) =>
        LogAsync(runId, LedgerKinds.UiResult, outcome: outcome,
            payload: JsonSerializer.Serialize(new { screen, commit, outcome, item = FSharpOption<string>.get_IsSome(handover.WorkItem) ? handover.WorkItem.Value : null }), ct: ct);

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
        var output = "";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using (var sandbox = await sandboxes.ForTeamAsync("integration", integration, ct))
        {
            if (sandbox.Kind != "docker") couldNot = "page pixels need the runner image: baselines depend on its platform and fonts";
            else
            {
                var report0 = Path.Combine(integration, ".vitest", "json", "output.json");
                var vitestDir = Path.Combine(integration, Factory.Harness.Registry.UiPreview.HtmlReportDir);
                if (Directory.Exists(vitestDir)) Directory.Delete(vitestDir, recursive: true); // nothing of an earlier run can be mistaken for this one
                var run = await sandbox.RunAsync(command, integration, timeout,
                    new Dictionary<string, string> { ["CHROME_PATH"] = UiPreviewRunner.ChromePath, ["CI"] = "1", ["FACTORY_RUN_ID"] = runId }, ct);
                tail = run.Tail(2000);
                output = run.Output;
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

            if (ranOk && report is not null && !failures.Any(f => f.Kind == "code"))
                try
                {
                    var found = UiRequestCheck.Check(integration, screen.Screen, screen.Spec.Text, registry);
                    if (found is null) { ranOk = false; notRun = $"the page tests wrote no request log for '{screen.Screen}' (the vitest config needs api.allowWrite)"; }
                    else foreach (var f in found.Failures) failures.Add((null, null, "request", f));
                }
                catch (Exception e) when (e is JsonException or IOException) { ranOk = false; notRun = "the request log could not be read: " + e.Message; }

            var outcome = failures.Count > 0 ? "fail" : ranOk ? "pass" : "could_not_run";
            var (element, blamed) = UiBlame.tasks(handover.Elements, tasks, screen.Component, screen.File);
            var elementId = FSharpOption<string>.get_IsSome(element) ? element.Value : null;
            var blamedIds = blamed.Select(t => t.Value).ToList();
            var lines = failures.Select(f => $"{screen.Screen}{(f.State is null ? "" : " / " + f.State)}: {f.Kind}{(f.Placement is null ? "" : " " + f.Placement)}"
                + (f.Kind is "code" or "request" ? " - " + f.Detail : "")).ToList();
            var summary = outcome switch
            {
                "pass" => $"ui:{screen.Screen} passed",
                "fail" => string.Join("\n", lines) + (blamedIds.Count == 0 ? "\nno task changes its element or writes " + screen.File : "\ntasks: " + string.Join(", ", blamedIds)),
                _ => "could_not_run: " + notRun,
            };

            // What the fixing agent is told, per state: text (diff, steps) and picture paths (kept under the run dir). ARIA failing means no picture.
            var feedback = new Dictionary<string, UiStateFeedback>();
            foreach (var state in failures.Where(f => f.State is not null).Select(f => f.State!).Distinct())
            {
                var fb = UiFeedback.forState(screen.Screen, state,
                    ListModule.OfSeq(failures.Where(f => f.State == state).Select(f => new UiFailure(state, f.Kind, f.Placement is null ? FSharpOption<string>.None : FSharpOption<string>.Some(f.Placement), f.Detail ?? ""))), output);
                var kept = KeepImages(integration, screen.Screen, state, fb.Images);
                feedback[state] = new UiStateFeedback(fb.Text, ListModule.OfSeq(kept));
            }
            var told = new HashSet<(string, string)>(); // the state's feedback rides on the first row of each (task, state)
            foreach (var f in failures)
                foreach (var task in blamedIds)
                {
                    var first = f.State is not null && told.Add((task, f.State));
                    var fb = f.State is not null && first ? feedback[f.State] : null;
                    await ledger.AppendAsync(new LedgerEvent(runId, "S2", "integration_verify", LedgerKinds.UiCheckFailed, TaskId: task, Element: elementId, Outcome: "failed",
                        Payload: JsonSerializer.Serialize(new
                        {
                            screen = screen.Screen, state = f.State, placement = f.Placement, kind = f.Kind, detail = f.Kind is "code" or "request" ? f.Detail : null, commit,
                            feedback = fb?.Text, images = fb is null ? null : fb.Images.ToArray(),
                        })), ct);
                }
            await KeepHtmlReportAsync(runId, screen.Screen, integration, ct);
            await LogAsync(runId, LedgerKinds.Verify, outcome: outcome == "pass" ? "passed" : "failed",
                payload: JsonSerializer.Serialize(new { name = "ui:" + screen.Screen, ExitCode = outcome == "pass" ? 0 : outcome == "fail" ? 1 : 3, kinds = failures.Select(f => f.Kind).Distinct() }), ct: ct);
            if (writesResult) await WriteResultAsync(runId, screen.Screen, commit, outcome, handover, ct);
            outcomes.Add(new VerifyOutcome("ui:" + screen.Screen, outcome == "pass" ? 0 : outcome == "fail" ? 1 : 3, sw.ElapsedMilliseconds, summary));
        }
        return outcomes;
    }

    /// Copies each diff image (and the actual picture beside it) to `evidence/ui/<screen>/<state>/`, so they outlive the worktree; returns
    /// the diff paths relative to the run dir. A picture the run did not write is left out.
    private List<string> KeepImages(string integration, string screen, string state, IEnumerable<string> relative)
    {
        var kept = new List<string>();
        if (runDir is null) return kept;
        foreach (var rel in relative)
        {
            var root = Path.GetFullPath(integration);
            var src = Path.GetFullPath(Path.Combine(integration, rel));
            if (!src.StartsWith(root + Path.DirectorySeparatorChar) || !File.Exists(src)) continue;
            var to = Path.Combine(runDir, EvidenceRoot, screen, state);
            Directory.CreateDirectory(to);
            File.Copy(src, Path.Combine(to, Path.GetFileName(src)), overwrite: true);
            var actual = src.Replace("-diff-", "-actual-");
            if (actual != src && File.Exists(actual)) File.Copy(actual, Path.Combine(to, Path.GetFileName(actual)), overwrite: true);
            kept.Add($"{EvidenceRoot}/{screen}/{state}/{Path.GetFileName(src)}");
        }
        return kept;
    }

    /// The HTML report (with Vitest's trace viewer) copied to `evidence/ui/<screen>/html/` after every run, and a `verify` row `ui` / `evidence` that names it.
    /// A run that wrote none adds no row; that is never a failure.
    private async Task KeepHtmlReportAsync(string runId, string screen, string integration, CancellationToken ct)
    {
        var src = Path.Combine(integration, Factory.Harness.Registry.UiPreview.HtmlReportDir);
        if (runDir is null || !File.Exists(Path.Combine(src, "index.html"))) return;
        var rel = $"{EvidenceRoot}/{screen}/html";
        var dst = Path.Combine(runDir, rel);
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(dst, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to, overwrite: true);
        }
        await ledger.AppendAsync(new LedgerEvent(runId, "S2", "ui", LedgerKinds.Verify, Outcome: "evidence", Payload: JsonSerializer.Serialize(new { screen, path = rel })), ct);
    }
}
