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

/// UI mutation: each generated mutant (a token prop turned to another value, or a placement removed) is written into the page in a
/// scratch checkout and judged by the generated page tests. A mutant any page test fails on is killed; one every test passes on survived,
/// which says the tests do not look at what it changed. Report only: it writes `ui_mutants` rows and never fails the run.
public sealed class UiMutantCheck(ILedger ledger, ISandboxFactory sandboxes, string command, int max)
{
    public sealed record MutantResult(string Id, string Status, List<string>? KilledBy = null, string? Reason = null);

    private ValueTask LogAsync(string runId, string outcome, object payload, CancellationToken ct) =>
        ledger.AppendAsync(new LedgerEvent(runId, "S2", "ui_mutants", LedgerKinds.Verify, Outcome: outcome, Payload: JsonSerializer.Serialize(payload)), ct);

    public async Task<Dictionary<string, List<MutantResult>>> RunAsync(string runId, Handover handover, FSharpOption<string> tokens, GitWorkspace git, TimeSpan timeout, CancellationToken ct)
    {
        var results = new Dictionary<string, List<MutantResult>>();
        if (handover.Ui.IsEmpty) return results;
        var commit = await git.HeadAsync(git.IntegrationPath(runId), ct);
        BaselineCheckout? scratch = null;
        ICommandSandbox? sandbox = null;
        try
        {
            foreach (var screen in handover.Ui)
            {
                var (generated, notes) = UiMutants.generate(screen.Spec.Text, ListModule.OfSeq(screen.Components.Select(c => c.Text)), tokens);
                var all = generated.ToList();
                var cut = Math.Max(0, all.Count - max);
                var mutants = all.Take(max).ToList();
                var list = results[screen.Screen] = [];
                if (notes.Any() || cut > 0)
                    await LogAsync(runId, "note", new { screen = screen.Screen, notes = notes.ToList(), cut }, ct);
                if (mutants.Count == 0) continue;

                scratch ??= await git.BaselineCheckoutAsync(runId, ct);
                var pagePath = Path.Combine(scratch.Path, screen.File);
                if (!File.Exists(pagePath))
                {
                    list.AddRange(mutants.Select(m => new MutantResult(m.Id, "could_not_run", Reason: "the page file does not exist")));
                }
                else
                {
                    var original = await File.ReadAllTextAsync(pagePath, ct);
                    var states = UiSpecStates.ofSpec(screen.Spec.Text).Length;
                    sandbox ??= await sandboxes.ForTeamAsync("ui-mutants", scratch.Path, ct);
                    foreach (var m in mutants)
                    {
                        if (sandbox.Kind != "docker") { list.Add(new(m.Id, "could_not_run", Reason: "page tests need the runner image")); continue; }
                        var applied = UiMutants.apply(original, m);
                        if (applied.IsError) { list.Add(new(m.Id, "skipped", Reason: applied.ErrorValue)); continue; }
                        list.Add(await TryAsync(runId, screen, m, applied.ResultValue, pagePath, original, scratch.Path, sandbox, states, timeout, ct));
                    }
                }

                var killed = list.Count(r => r.Status == "killed");
                var survived = list.Count(r => r.Status == "survived");
                var judged = killed + survived;
                var outcome = judged == 0 ? "could_not_run" : survived == 0 ? "all_killed" : "survivors";
                await LogAsync(runId, outcome, new
                {
                    screen = screen.Screen, commit, score = judged == 0 ? (double?)null : Math.Round((double)killed / judged, 4),
                    mutants = list.Select(r => new { id = r.Id, status = r.Status, killed_by = r.KilledBy, reason = r.Reason }),
                }, ct);
            }
        }
        finally
        {
            if (sandbox is not null) await sandbox.DisposeAsync();
            if (scratch is not null) await scratch.DisposeAsync();
        }
        return results;
    }

    private async Task<MutantResult> TryAsync(string runId, UiScreen screen, UiMutant m, string mutated, string pagePath, string original, string root,
        ICommandSandbox sandbox, int states, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await File.WriteAllTextAsync(pagePath, mutated, ct);
            var report0 = Path.Combine(root, ".vitest", "json", "output.json");
            if (File.Exists(report0)) File.Delete(report0);
            var run = await sandbox.RunAsync(command, root, timeout,
                new Dictionary<string, string> { ["CHROME_PATH"] = UiPreviewRunner.ChromePath, ["CI"] = "1", ["FACTORY_RUN_ID"] = runId }, ct);
            string? report = null;
            var path = UiPreview.ReportPath(run.Output) ?? report0;
            if (File.Exists(path)) report = await File.ReadAllTextAsync(path, ct);
            else if (run.Output.TrimStart().StartsWith('{')) report = run.Output;
            if (report is null) return new(m.Id, "could_not_run", Reason: run.TimedOut ? "the page tests timed out" : $"no report (exit {run.ExitCode})");
            try
            {
                var failed = UiReport.failures(screen.Screen, report).Select(f => $"{f.State} {f.Kind}").Distinct().ToList();
                if (failed.Count > 0) return new(m.Id, "killed", failed);
                return UiReport.executed(screen.Screen, report) < 3 * states
                    ? new(m.Id, "could_not_run", Reason: "only some of the page tests ran")
                    : new(m.Id, "survived");
            }
            catch (JsonException) { return new(m.Id, "could_not_run", Reason: "the report could not be read"); }
        }
        finally { await File.WriteAllTextAsync(pagePath, original, ct); }
    }
}
