using System.Text.Json;
using Factory.Core;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;

namespace Factory.Harness.Registry;

/// One trace per ended task, read from a run's own ledger: what ran it (models, procedures), what it did, how it
/// ended and why. Pure: rows written before a field existed read as zero or none, and a row it cannot read is skipped.
public static class TraceReader
{
    /// `limits`: the run's task limits, as a resume replays with. `jira`: the work item the run is for, if any.
    public static IReadOnlyList<TaskTrace> Read(IReadOnlyList<LedgerEvent> run, Limits limits, string? jira = null)
    {
        var procedures = Procedures(run);
        var shares = RegistryLedgerProjector.PlannerShares(run);
        var traces = new List<TaskTrace>();
        foreach (var task in run.Where(e => e.TaskId is not null).GroupBy(e => e.TaskId!))
        {
            var payloads = task.Where(e => e.Kind == LedgerKinds.TaskEvent && e.Payload is not null).Select(e => e.Payload!).ToList();
            if (payloads.Count == 0) continue;
            ReplayedTask replayed;
            try { replayed = Factory.Core.Core.ReplayTrace(limits, payloads); }
            catch (Exception ex) when (ex is FactoryRejectedException or InvalidOperationException) { continue; }

            var turns = task.Where(e => e.Kind == LedgerKinds.Turn).Select(t => new TraceTurn(
                ModelIdOf(t) ?? t.Model ?? "unknown", t.TokensIn ?? 0, t.TokensOut ?? 0, ToolCallsOf(t))).ToList();
            var (bundleId, bundleProcedures) = Bundle(task);
            var (_, sharedIn, sharedOut) = shares.TryGetValue(task.Key, out var s) ? s : (0, 0L, 0L);
            var trace = Factory.Core.Core.BuildTrace(new TraceInput(
                run: task.First().RunId,
                task: task.Key,
                end: replayed.End,
                lastDecisionEscalated: task.LastOrDefault(e => e.Kind == LedgerKinds.Configurator)?.Outcome == "Escalate",
                turns: ListModule.OfSeq(turns),
                verifyAttempts: replayed.VerifyAttempts,
                reviewRounds: replayed.ReviewRounds,
                scopeReverts: task.Count(e => e.Kind == LedgerKinds.ScopeViolation && e.Outcome == "reverted"),
                sharedTokensIn: sharedIn,
                sharedTokensOut: sharedOut,
                // A task with no fetched bundle (none source, a miss, an unreadable row) is recorded as none, not left out.
                bundleId: Option(bundleId),
                traceId: Option(task.FirstOrDefault(e => e.TraceId is not null)?.TraceId),
                jira: Option(jira),
                procedures: ListModule.OfSeq(procedures.Concat(bundleProcedures))));
            if (trace is not null) traces.Add(trace);
        }
        return traces;
    }

    /// The task's latest context row: its bundle_id and procedures (copied as given), or none when it missed or cannot be read.
    private static (string? Id, List<ProcedureRef> Procedures) Bundle(IEnumerable<LedgerEvent> task) =>
        Payload(task.LastOrDefault(e => e.Kind == LedgerKinds.Context), p =>
        {
            var id = Text(p, "bundle_id");
            if (id == "") return ((string?)null, new List<ProcedureRef>());
            var refs = p.TryGetProperty("procedures", out var ps) && ps.ValueKind == JsonValueKind.Array
                ? ps.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)
                    .Select(x => new ProcedureRef(Text(x, "id"), Text(x, "version"), Text(x, "hash"))).Where(r => r.Id != "" && r.Hash != "").ToList()
                : [];
            return (id, refs);
        }, ((string?)null, new List<ProcedureRef>()));

    /// The latest attempt's procedures: run_started payload {procedures:[{id, version, hash}]}.
    private static FSharpList<ProcedureRef> Procedures(IReadOnlyList<LedgerEvent> run)
    {
        var start = run.LastOrDefault(e => e.Kind == LedgerKinds.RunStarted && e.TaskId is null);
        var refs = Payload(start, p =>
            p.TryGetProperty("procedures", out var ps) && ps.ValueKind == JsonValueKind.Array
                ? ps.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Select(x =>
                    new ProcedureRef(Text(x, "id"), Text(x, "version"), Text(x, "hash"))).Where(r => r.Id != "" && r.Hash != "").ToList()
                : [], new List<ProcedureRef>());
        return ListModule.OfSeq(refs);
    }

    private static string? ModelIdOf(LedgerEvent turn) =>
        Payload<string?>(turn, p => p.TryGetProperty("model_id", out var m) && m.ValueKind == JsonValueKind.String && m.GetString() is { Length: > 0 } id ? id : null, null);

    private static int ToolCallsOf(LedgerEvent turn) =>
        Payload(turn, p => p.TryGetProperty("tool_calls", out var n) && n.TryGetInt32(out var v) && v > 0 ? v : 0, 0);

    private static string Text(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static T Payload<T>(LedgerEvent? e, Func<JsonElement, T> read, T fallback)
    {
        if (e?.Payload is null) return fallback;
        try
        {
            using var doc = JsonDocument.Parse(e.Payload);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? read(doc.RootElement) : fallback;
        }
        catch (JsonException) { return fallback; }
    }

    private static FSharpOption<string> Option(string? s) => s is null ? FSharpOption<string>.None : FSharpOption<string>.Some(s);
}
