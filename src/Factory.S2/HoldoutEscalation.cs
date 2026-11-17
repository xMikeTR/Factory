using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Harness.Registry;
using Microsoft.FSharp.Core;

namespace Factory.S2;

using Core = Factory.Core.Core;

/// A holdout that keeps failing is a question for a person, not for another build. A holdout is counted by (screen) or (screen, criterion),
/// across the runs of one work item (or of one handover when it has no work item), this run included: past `FACTORY_HOLDOUT_RETRIES`
/// failing runs the run raises a needs_decision instead of asking the build to try again.
public static class HoldoutEscalation
{
    private sealed record Failure(string Key, string Screen, string? Criterion, string State, string? TaskId, string? Element);

    private static IEnumerable<Failure> FailuresIn(IEnumerable<LedgerEvent> events)
    {
        foreach (var e in events.Where(e => e.Kind == LedgerKinds.HoldoutFailed && e.Payload is not null))
        {
            using var doc = JsonDocument.Parse(e.Payload!);
            string? S(string n) => doc.RootElement.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (S("screen") is not { } screen) continue;
            var criterion = S("criterion");
            yield return new(criterion is null ? screen : screen + "/" + criterion, screen, criterion, S("state") ?? "", e.TaskId, e.Element);
        }
    }

    private static async Task<List<LedgerEvent>> RowsAsync(ILedger ledger, string runId, CancellationToken ct)
    {
        var rows = new List<LedgerEvent>();
        await foreach (var e in ledger.ReadAsync(runId, ct)) rows.Add(e);
        return rows;
    }

    private static bool SameItem(Handover a, Handover b) =>
        FSharpOption<string>.get_IsSome(a.WorkItem) || FSharpOption<string>.get_IsSome(b.WorkItem)
            ? FSharpOption<string>.get_IsSome(a.WorkItem) && FSharpOption<string>.get_IsSome(b.WorkItem) && a.WorkItem.Value == b.WorkItem.Value
            : a.Id == b.Id;

    /// The question raised when this run's failing holdout has now failed more times than the cap allows (the needs_decision row is written
    /// here); null when it has not. The run ends integration_failed with that text.
    public static async Task<string?> CheckAsync(FactoryServices svc, string runId, Handover handover, CancellationToken ct)
    {
        var mine = FailuresIn(await RowsAsync(svc.Ledger, runId, ct)).ToList();
        if (mine.Count == 0) return null;

        var counts = mine.Select(f => f.Key).Distinct().ToDictionary(k => k, _ => 1);
        var runs = Path.Combine(svc.Settings.StateDir, "runs");
        if (Directory.Exists(runs))
            foreach (var dir in Directory.GetDirectories(runs).Select(Path.GetFileName).OfType<string>().Where(n => n != runId))
            {
                var copy = svc.Settings.HandoverCopyPath(dir);
                if (!File.Exists(copy)) continue;
                Handover other;
                try { other = Core.ParseHandover(await File.ReadAllTextAsync(copy, ct)); } catch (Exception e) when (e is JsonException or InvalidOperationException) { continue; }
                if (!SameItem(handover, other)) continue;
                foreach (var key in FailuresIn(await RowsAsync(svc.Ledger, dir, ct)).Select(f => f.Key).Distinct().Where(counts.ContainsKey))
                    counts[key]++;
            }

        foreach (var f in mine)
        {
            var n = counts[f.Key];
            if (!HoldoutCap.exceeded(svc.Settings.HoldoutRetries, n)) continue;
            var question = $"holdout {f.Screen}{(f.Criterion is null ? "" : "/" + f.Criterion)}/{f.State} failed {n} times";
            var raised = (await RowsAsync(svc.Ledger, runId, ct)).Where(e => e.Kind == LedgerKinds.NeedsDecision && e.Payload is not null)
                .Select(e => JsonDocument.Parse(e.Payload!).RootElement.TryGetProperty("id", out var i) ? i.GetString() : null).OfType<string>().ToList();
            var id = await new RegistryNeedsDecisions(svc.Settings.RegistryPath).NextIdAsync(raised, ct);
            await svc.LogAsync(runId, "integration_verify", LedgerKinds.NeedsDecision, outcome: "raised", taskId: f.TaskId,
                payload: JsonSerializer.Serialize(new
                {
                    id,
                    question,
                    source = "holdout",
                    elements = f.Element is null ? Array.Empty<string>() : [f.Element],
                    options = new[]
                    {
                        new { id = "A", summary = "keep the holdout; a person writes guidance for the build", consequence = "the next run builds with that guidance and is judged by the same holdout" },
                        new { id = "B", summary = "the holdout is wrong; change it in designc and freeze again", consequence = "a new freeze carries the corrected holdout; this work item's failure count starts over with it" },
                    },
                }), ct: ct);
            return $"{question} ({id}): the build keeps missing data it was never shown, so a person decides before another run.";
        }
        return null;
    }
}
