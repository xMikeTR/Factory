using System.Text.Json;

namespace Factory.Harness.Registry;

/// designc's `stage_verdict` (D7): what each verification stage said about a task and screen, for System 3's time to
/// signal, escape rate and veto and recovery. Built only from state names, kinds, placements and criterion ids: no
/// detail, tail, trace, path or feedback text from a source row ever goes into a verdict.
public static partial class RegistryLedgerProjector
{
    private sealed record Failure(string? State = null, string? Kind = null, string? Placement = null, string? Criterion = null);

    private sealed record Verdict(
        LedgerEvent Source, string Stage, string Outcome, string? Task = null, string? Element = null, string? Screen = null,
        int? Round = null, IReadOnlyList<Failure>? Failures = null);

    private static IEnumerable<(DateTimeOffset, RegistryEvent)> StageVerdicts(IReadOnlyList<LedgerEvent> run, HandoverRef handover)
    {
        var requests = VerifyRequests(run);
        var verdicts = new List<Verdict>();
        verdicts.AddRange(UiVerdicts(run));
        verdicts.AddRange(HoldoutVerdicts(run));
        verdicts.AddRange(AcceptanceVerdicts(run));
        verdicts.AddRange(ReviewVerdicts(run));
        verdicts.AddRange(QaVerdicts(run));
        foreach (var v in verdicts)
            yield return (v.Source.Ts, Row(Pipeline, "stage_verdict", v.Source, w =>
            {
                w.WriteString("run", v.Source.RunId);
                w.WriteString("stage", v.Stage);
                w.WriteString("outcome", v.Outcome);
                if (ChangeTs(requests, v) is { } changed) w.WriteNumber("change_ts", changed); else w.WriteNull("change_ts");
                if (v.Task is not null) w.WriteString("task", v.Task);
                if (v.Element is not null) w.WriteString("element", v.Element);
                if (v.Screen is not null) w.WriteString("screen", v.Screen);
                if (handover.WorkItem is not null) w.WriteString("item", handover.WorkItem);
                if (v.Round is { } round) w.WriteNumber("round", round);
                if (v.Failures is { Count: > 0 })
                {
                    w.WriteStartArray("failures");
                    foreach (var f in v.Failures)
                    {
                        w.WriteStartObject();
                        if (f.State is not null) w.WriteString("state", f.State);
                        if (f.Kind is not null) w.WriteString("kind", f.Kind);
                        if (f.Placement is not null) w.WriteString("placement", f.Placement);
                        if (f.Criterion is not null) w.WriteString("criterion", f.Criterion);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
            }));
    }

    /// When each task last asked to be verified: the task-event rows of type verify_requested (what the agent changed is checked from then).
    private static List<(string Task, DateTimeOffset Ts)> VerifyRequests(IReadOnlyList<LedgerEvent> run)
    {
        var requests = new List<(string, DateTimeOffset)>();
        foreach (var e in run.Where(e => e is { Kind: LedgerKinds.TaskEvent, TaskId: not null, Payload: not null }))
        {
            using var doc = TryParse(e.Payload!);
            if (doc is not null && Str(doc.RootElement, "type") == "verify_requested") requests.Add((e.TaskId!, e.Ts));
        }
        return requests;
    }

    /// A task's verdict: that task's latest request not after the verdict. A run-level one: the latest of any task. None: null, so time to signal skips it.
    private static long? ChangeTs(List<(string Task, DateTimeOffset Ts)> requests, Verdict v)
    {
        var before = requests.Where(r => r.Ts <= v.Source.Ts && (v.Task is null || r.Task == v.Task)).ToList();
        return before.Count == 0 ? null : before.Max(r => r.Ts).ToUnixTimeMilliseconds();
    }

    private static string? Str(JsonElement obj, string name) =>
        Prop(obj, name) is { ValueKind: JsonValueKind.String } v && v.GetString() is { Length: > 0 } s ? s : null;

    /// One verdict per (stage, task, screen) from rows that blame a task; the failures of all its rows merged, the first row's time.
    private static IEnumerable<Verdict> Blamed(IEnumerable<(LedgerEvent E, string Stage, string? Screen, Failure F)> rows) =>
        // an acceptance holdout (it names a criterion) is a different case from a UI holdout on the same screen
        rows.GroupBy(r => (r.Stage, Task: r.E.TaskId, r.Screen, ByCriterion: r.F.Criterion is not null)).Select(g =>
        {
            var first = g.First().E;
            var failures = g.Select(r => r.F).Distinct().ToList();
            return new Verdict(first, g.Key.Stage, "fail", g.Key.Task, first.Element, g.Key.Screen, null, failures);
        });

    private static IEnumerable<Verdict> UiVerdicts(IReadOnlyList<LedgerEvent> run)
    {
        var failed = new List<(LedgerEvent, string, string?, Failure)>();
        foreach (var e in run.Where(e => e.Kind == LedgerKinds.UiCheckFailed && e.TaskId is not null && e.Payload is not null))
        {
            using var doc = TryParse(e.Payload!);
            if (doc is null || Str(doc.RootElement, "screen") is not { } screen || Str(doc.RootElement, "kind") is not { } kind) continue;
            var stage = kind == "code" ? "ui-code" : "ui-visual";
            failed.Add((e, stage, screen, new Failure(Str(doc.RootElement, "state"), kind, Str(doc.RootElement, "placement"))));
        }
        foreach (var v in Blamed(failed)) yield return v;

        // A screen's ui: verify says nothing more once a task was blamed for it. Otherwise it passed, or could not run (exit 3).
        var blamedScreens = failed.Select(f => f.Item3).ToHashSet();
        var latest = new Dictionary<string, (LedgerEvent E, string Outcome)>();
        foreach (var e in run.Where(e => e.Kind == LedgerKinds.Verify && e.Payload is not null))
        {
            using var doc = TryParse(e.Payload!);
            if (doc is null || Str(doc.RootElement, "name") is not { } name || !name.StartsWith("ui:")) continue;
            var screen = name[3..];
            if (blamedScreens.Contains(screen)) continue;
            if (e.Outcome == "passed") latest[screen] = (e, "pass");
            else if (Prop(doc.RootElement, "ExitCode") is { ValueKind: JsonValueKind.Number } code && code.GetInt32() == 3) latest[screen] = (e, "could_not_run");
        }
        foreach (var (screen, (e, outcome)) in latest)
        {
            yield return new Verdict(e, "ui-code", outcome, Screen: screen);
            yield return new Verdict(e, "ui-visual", outcome, Screen: screen);
        }
    }

    private static IEnumerable<Verdict> HoldoutVerdicts(IReadOnlyList<LedgerEvent> run)
    {
        var failed = new List<(LedgerEvent, string, string?, Failure)>();
        foreach (var e in run.Where(e => e.Kind == LedgerKinds.HoldoutFailed && e.TaskId is not null && e.Payload is not null))
        {
            using var doc = TryParse(e.Payload!);
            if (doc is null || Str(doc.RootElement, "screen") is not { } screen) continue;
            failed.Add((e, "holdout", screen, new Failure(Str(doc.RootElement, "state"), Criterion: Str(doc.RootElement, "criterion"))));
        }
        foreach (var v in Blamed(failed)) yield return v;

        var uiBlamed = failed.Where(f => f.Item4.Criterion is null).Select(f => f.Item3).ToHashSet();
        var acceptanceBlamed = failed.Any(f => f.Item4.Criterion is not null);
        foreach (var e in run.Where(e => e is { Kind: LedgerKinds.Verify, Payload: not null }))
        {
            using var doc = TryParse(e.Payload!);
            if (doc is null || Str(doc.RootElement, "name") != "holdout" || Str(doc.RootElement, "outcome") is not { } outcome) continue;
            var screen = Str(doc.RootElement, "screen");
            var acceptance = Str(doc.RootElement, "kind") == "acceptance";
            if (outcome == "pass" || outcome == "could_not_run")
                yield return new Verdict(e, "holdout", outcome, Screen: screen);
            else if (outcome == "fail" && (acceptance ? !acceptanceBlamed : !uiBlamed.Contains(screen)))
            {
                // Failed, but no task changes the page: the verdict still counts, for the run.
                var states = Prop(doc.RootElement, "states_failed") is { ValueKind: JsonValueKind.Array } a
                    ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => new Failure(x.GetString())).ToList() : [];
                yield return new Verdict(e, "holdout", "fail", Screen: screen, Failures: states);
            }
        }
    }

    /// A frozen spec that failed blames tasks (`ui_acceptance_failed`); the visible acceptance run's own verify row gives the
    /// run-level pass, or a fail when it blamed no task. The holdout's acceptance run is ledgered under its own stage and skipped.
    private static IEnumerable<Verdict> AcceptanceVerdicts(IReadOnlyList<LedgerEvent> run)
    {
        var failed = new List<(LedgerEvent, string, string?, Failure)>();
        foreach (var e in run.Where(e => e.Kind == LedgerKinds.UiAcceptanceFailed && e.TaskId is not null && e.Payload is not null))
        {
            using var doc = TryParse(e.Payload!);
            if (doc is null || Str(doc.RootElement, "ac") is not { } ac) continue;
            failed.Add((e, "acceptance", null, new Failure(Criterion: ac)));
        }
        foreach (var v in Blamed(failed)) yield return v;

        foreach (var e in run.Where(e => e is { Kind: LedgerKinds.Verify, Stage: "integration_verify", Payload: not null }))
        {
            using var doc = TryParse(e.Payload!);
            if (doc is null || Str(doc.RootElement, "name") != "acceptance") continue;
            if (e.Outcome == "passed") yield return new Verdict(e, "acceptance", "pass");
            else if (e.Outcome == "failed" && failed.Count == 0) yield return new Verdict(e, "acceptance", "fail");
        }
    }

    /// One verdict per review round. A round's number is in its payload; the element is the task's (review rows carry none).
    private static IEnumerable<Verdict> ReviewVerdicts(IReadOnlyList<LedgerEvent> run)
    {
        foreach (var e in run.Where(e => e is { Kind: LedgerKinds.Review, TaskId: not null, Payload: not null }))
        {
            var outcome = e.Outcome switch { "passed" or "accepted" => "pass", "blocked" => "fail", "malformed" => "could_not_run", _ => null };
            using var doc = TryParse(e.Payload!);
            if (outcome is null || doc is null || Prop(doc.RootElement, "round") is not { ValueKind: JsonValueKind.Number } r || !r.TryGetInt32(out var round) || round < 1) continue;
            var element = run.LastOrDefault(t => t.Kind == LedgerKinds.TaskEvent && t.TaskId == e.TaskId && t.Element is not null)?.Element;
            yield return new Verdict(e, "review", outcome, e.TaskId, element, Round: round);
        }
    }

    /// QA runs once on the integrated result, so its verdict is the run's.
    private static IEnumerable<Verdict> QaVerdicts(IReadOnlyList<LedgerEvent> run)
    {
        foreach (var e in run.Where(e => e is { Kind: LedgerKinds.Verify, Stage: "qa" }))
        {
            var outcome = e.Outcome switch { "passed" => "pass", "failed" => "fail", "timeout" => "could_not_run", _ => null };
            if (outcome is not null) yield return new Verdict(e, "qa", outcome);
        }
    }
}
