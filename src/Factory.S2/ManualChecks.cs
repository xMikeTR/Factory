using System.Text.Json;
using Factory.Core;
using Factory.Harness;

namespace Factory.S2;

/// `factory-s2 verify`: a person records a manual check of a run in its ledger, done or deferred
/// with a note. The merge step reads these rows beside `/verified` PR comments, so a dry-run PR
/// can clear a manual check too; resuming the run with the same id then reaches the merge.
public static class ManualChecks
{
    private sealed record Row(string Check, string By, string Outcome, string? Note);

    /// The run's manual checks as the merge names them: "<task>: <check name>".
    public static IReadOnlyList<string> Of(DispatchPlan dispatch) =>
        dispatch.Waves.SelectMany(w => w.Teams).SelectMany(t => t.Tasks)
            .SelectMany(t => t.Verify.OfType<VerifyEntry.Manual>().Select(m => $"{t.Id.Value}: {m.Item.Name}")).ToList();

    /// Null when recorded, else why nothing was recorded.
    public static async Task<string?> RecordAsync(ILedger ledger, string runId, DispatchPlan dispatch, MergeEnforcement enforcement,
        string check, string by, string outcome, string? note, CancellationToken ct)
    {
        var known = Of(dispatch);
        if (!known.Contains(check)) return $"no manual check '{check}' in run {runId} ({(known.Count == 0 ? "it has none" : string.Join(", ", known))})";
        if (string.IsNullOrWhiteSpace(by)) return "--by needs the name of the person who did the check";
        if (outcome is not ("done" or "deferred")) return $"--outcome is done or deferred, not '{outcome}'";
        if (outcome == "deferred" && string.IsNullOrWhiteSpace(note)) return "a deferred check needs --note saying why";
        if (enforcement is MergeEnforcement.Tiered t && Merge.countedManual(enforcement, Microsoft.FSharp.Collections.ListModule.OfSeq([ToRecord(new(check, by, outcome, note))])).IsEmpty)
            return $"{by} is not a merge approver ({string.Join(", ", t.approvers)})";
        await ledger.AppendAsync(new LedgerEvent(runId, "S2", "merge", LedgerKinds.ManualCheck, TaskId: check.Split(':')[0], Outcome: outcome,
            Payload: JsonSerializer.Serialize(new Row(check, by.Trim(), outcome, string.IsNullOrWhiteSpace(note) ? null : note.Trim()))), ct);
        return null;
    }

    public static async Task<IReadOnlyList<ManualRecord>> ReadAsync(ILedger ledger, string runId, CancellationToken ct)
    {
        var records = new List<ManualRecord>();
        await foreach (var e in ledger.ReadAsync(runId, ct))
            if (e.Kind == LedgerKinds.ManualCheck && e.Payload is { } p && JsonSerializer.Deserialize<Row>(p) is { } row)
                records.Add(ToRecord(row));
        return records;
    }

    /// "<check>, deferred by <who>: <note>" for each counted deferral, for the PR body and the merge row.
    public static IReadOnlyList<string> Deferred(MergeEnforcement enforcement, IEnumerable<ManualRecord> records) =>
        Merge.countedManual(enforcement, Microsoft.FSharp.Collections.ListModule.OfSeq(records))
            .Where(r => r.Outcome.IsDeferred).Select(r => $"{r.Check}, deferred by {r.By}: {((ManualOutcome.Deferred)r.Outcome).note}").ToList();

    private static ManualRecord ToRecord(Row r) =>
        new(r.Check, r.By, r.Outcome == "deferred" ? ManualOutcome.NewDeferred(r.Note ?? "") : ManualOutcome.Verified);
}
