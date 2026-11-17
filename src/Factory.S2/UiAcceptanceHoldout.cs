using System.Diagnostics;
using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.S2.Git;
using Factory.S2.Verify;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;

namespace Factory.S2;

/// The acceptance holdout: cases the criterion's author marked `holdout: true`. The handover names them by hash only; the Factory process
/// asks designc (`designc-core holdout-spec`, headless, from FACTORY_DESIGNC_DIR) for the generated spec, checks it against the hash the freeze
/// recorded, writes it into a scratch copy of the integrated branch and runs it there. The runner's output stays in the scratch copy: the
/// ledger and the briefs get screen, criterion and state only.
public sealed class UiAcceptanceHoldout(FactoryServices svc, string? designcDir)
{
    public sealed record Generated(string Text, string Sha256, string State);
    private sealed record Planned(string Path, string Text, string Screen, string Criterion, string State);

    private static string Fixed(string why) => "could_not_run: " + why;

    private Task<(Generated? Spec, string? Why)> GenerateAsync(UiHoldoutCase c, CancellationToken ct) =>
        GenerateSpecAsync(designcDir!, svc.Settings.RegistryPath, c, ct);

    /// Runs designc's `holdout-spec --json` for one case and parses its output.
    public static async Task<(Generated? Spec, string? Why)> GenerateSpecAsync(string designcDir, string registryPath, UiHoldoutCase c, CancellationToken ct)
    {
        var script = Path.Combine(designcDir, "scripts", "designc-core.js");
        if (!File.Exists(script)) return (null, "designc's holdout-spec script was not found in FACTORY_DESIGNC_DIR");
        var psi = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { script, "holdout-spec", "--registry", registryPath, "--criterion", c.Criterion, "--case", c.Case.ToString(), "--json" }) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return (null, "node did not start");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(1));
            var stdout = p.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = p.StandardError.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token);
            var text = await stdout;
            await stderr;
            if (p.ExitCode != 0) return (null, $"designc holdout-spec exited {p.ExitCode}");
            using var doc = JsonDocument.Parse(text);
            return (new Generated(doc.RootElement.GetProperty("spec").GetString()!, doc.RootElement.GetProperty("sha256").GetString()!,
                doc.RootElement.TryGetProperty("state", out var st) ? st.GetString() ?? "" : ""), null);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or JsonException or KeyNotFoundException || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return (null, "designc holdout-spec could not run");
        }
    }

    private static string Dir(Handover h, string criterion)
    {
        var visible = h.AcceptanceSpecs.Select(a => AcceptanceSpecs.parse(a.Path)).Where(FSharpOption<Tuple<string, string>>.get_IsSome)
            .Select(o => o.Value).FirstOrDefault(t => t.Item2 == criterion);
        if (visible is not null) return visible.Item1;
        var element = h.AcceptanceCriteria.FirstOrDefault(c => c.Id == criterion)?.Element ?? criterion;
        var match = h.Elements.FirstOrDefault(e => e.Id.Value == element || e.Name == element);
        return AcceptanceSpecs.slug(match?.Name ?? element.Split('/').Last());
    }

    public async Task<HoldoutRun> RunAsync(string runId, Handover handover, FSharpList<PlannedTask> tasks, GitWorkspace git, TimeSpan timeout, CancellationToken ct)
    {
        var outcomes = new List<VerifyOutcome>();
        var screens = new Dictionary<string, string>();
        var cases = handover.Ui.SelectMany(s => s.HoldoutCases.Select(c => (Screen: s.Screen, Case: c))).ToList();
        if (cases.Count == 0) return new HoldoutRun(outcomes, screens);

        Task NotRunAsync(string why)
        {
            foreach (var screen in cases.Select(c => c.Screen).Distinct()) screens[screen] = "could_not_run";
            outcomes.Add(new VerifyOutcome("holdout:acceptance", 3, 0, Fixed(why)));
            return svc.Ledger.AppendAsync(new LedgerEvent(runId, "S2", "integration_verify", LedgerKinds.Verify, Outcome: "failed",
                Payload: JsonSerializer.Serialize(new { name = "holdout", kind = "acceptance", outcome = "could_not_run" })), ct).AsTask();
        }

        if (designcDir is null) { await NotRunAsync("acceptance holdout cases are frozen but FACTORY_DESIGNC_DIR is not set"); return new HoldoutRun(outcomes, screens); }

        var planned = new List<Planned>();
        foreach (var (screen, c) in cases)
        {
            var (spec, why) = await GenerateAsync(c, ct);
            if (spec is null) { await NotRunAsync(why!); return new HoldoutRun(outcomes, screens); }
            if (!string.Equals(spec.Sha256, c.Sha256, StringComparison.OrdinalIgnoreCase))
            { await NotRunAsync($"the holdout case {c.Criterion}/{c.Case} changed since the freeze"); return new HoldoutRun(outcomes, screens); }
            planned.Add(new Planned($"{AcceptanceSpecs.root}{Dir(handover, c.Criterion)}/{c.Criterion}.holdout-{c.Case}.spec.ts", spec.Text, screen, c.Criterion, c.State));
        }

        var sw = Stopwatch.StartNew();
        List<Factory.Harness.Verify.PlaywrightReport.Failure> failures;
        VerifyOutcome result;
        await using (var scratch = await git.BaselineCheckoutAsync(runId, ct))
        {
            foreach (var p in planned)
            {
                var file = Path.Combine(scratch.Path, p.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllTextAsync(file, p.Text, ct);
            }
            var paths = planned.Select(p => p.Path).ToList();
            var command = svc.Settings.AcceptanceCommand + " " + string.Join(" ", paths.Select(p => "'" + p + "'"));
            var (outcome, found) = await svc.Verify.AcceptanceAsync(runId, command, scratch.Path, paths, ct, stage: "holdout_acceptance", logTail: false);
            result = outcome;
            failures = found.ToList();
        }

        var commit = await git.HeadAsync(git.IntegrationPath(runId), ct);
        foreach (var screen in cases.Select(c => c.Screen).Distinct()) screens[screen] = "pass";
        var lines = new List<string>();
        foreach (var f in failures)
        {
            var p = planned.FirstOrDefault(x => x.Path == f.File);
            if (p is null) continue;
            screens[p.Screen] = "fail";
            var failure = AcceptanceSpecs.failure(handover.Elements, p.Path);
            var element = failure is not null && FSharpOption<SpecFailure>.get_IsSome(failure) && FSharpOption<ElementSpec>.get_IsSome(failure.Value.Element) ? failure.Value.Element.Value.Id.Value : null;
            var blamed = failure is not null && FSharpOption<SpecFailure>.get_IsSome(failure)
                ? AcceptanceSpecs.translationErrors(tasks, failure.Value).Select(e => e.Item.Value).ToList() : [];
            lines.Add($"holdout_failed: {p.Screen} (acceptance {p.Criterion}) in state {p.State}" + (blamed.Count == 0 ? "; no task changes its element" : "; tasks: " + string.Join(", ", blamed)));
            foreach (var task in blamed)
                await svc.Ledger.AppendAsync(new LedgerEvent(runId, "S2", "integration_verify", LedgerKinds.HoldoutFailed, TaskId: task, Element: element,
                    Outcome: "failed", Payload: JsonSerializer.Serialize(new { screen = p.Screen, criterion = p.Criterion, state = p.State })), ct);
        }

        var unreadable = failures.Count == 0 && result.ExitCode != 0;
        if (unreadable) foreach (var screen in screens.Keys.ToList()) screens[screen] = "could_not_run";
        await svc.Ledger.AppendAsync(new LedgerEvent(runId, "S2", "integration_verify", LedgerKinds.Verify, Outcome: result.ExitCode == 0 ? "passed" : "failed",
            Payload: JsonSerializer.Serialize(new { name = "holdout", kind = "acceptance", outcome = result.ExitCode == 0 ? "pass" : unreadable ? "could_not_run" : "fail", ms = sw.ElapsedMilliseconds, commit })), ct);
        if (result.ExitCode != 0)
            outcomes.Add(new VerifyOutcome("holdout:acceptance", unreadable ? 3 : 1, sw.ElapsedMilliseconds,
                unreadable ? Fixed("the acceptance holdout run failed without a readable report") : string.Join("\n", lines)));
        return new HoldoutRun(outcomes, screens);
    }
}
