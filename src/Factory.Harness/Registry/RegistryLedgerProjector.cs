using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Factory.Core;
using Microsoft.FSharp.Collections;

namespace Factory.Harness.Registry;

/// The handover a run's registry rows belong to, and the decisions whose checks decide how it closes.
public sealed record HandoverRef(string Id, IReadOnlyList<BoundDecision> Decisions)
{
    public static HandoverRef From(Handover h) => new(h.Id, h.Decisions.ToList());
}

/// One designc ledger row. Line is the canonical compact JSON written to {registry}/ledger/{Stream}.
public sealed record RegistryEvent(string Stream, string EventId, string Line);

/// Projects System 2's own ledger onto designc's registry streams. Pure: the same run gives the
/// same bytes, ts is the source row's ts, and event_id is a hash of the row's content, so a row
/// written twice is the same row (designc dedupes on event_id and flags V5 on a mismatch).
public static class RegistryLedgerProjector
{
    public const string By = "factory-s2";
    private const string Pipeline = "pipeline.jsonl";
    private const string Checks = "checks.jsonl";

    public static IReadOnlyList<RegistryEvent> Project(IReadOnlyList<LedgerEvent> run, HandoverRef handover)
    {
        var rows = new List<(DateTimeOffset Ts, RegistryEvent Row)>();
        rows.AddRange(Tasks(run, handover));
        foreach (var e in run)
        {
            if (e.Kind == LedgerKinds.NeedsDecision && NeedsDecision(e) is { } nd) rows.Add((e.Ts, nd));
            else if (e.Kind == LedgerKinds.Verify && e.Decision is not null && Check(e) is { } check) rows.Add((e.Ts, check));
        }
        if (HandoverClosed(run, handover) is { } closed) rows.Add(closed);
        // Stable: ties keep source order.
        return rows.OrderBy(r => r.Ts).Select(r => r.Row).ToList();
    }

    /// One row per task: its latest state, turn count and tokens so far.
    private static IEnumerable<(DateTimeOffset, RegistryEvent)> Tasks(IReadOnlyList<LedgerEvent> run, HandoverRef handover)
    {
        foreach (var task in run.Where(e => e.TaskId is not null).GroupBy(e => e.TaskId!))
        {
            var last = task.LastOrDefault(e => e.Kind == LedgerKinds.TaskEvent);
            if (last?.Outcome is null) continue;
            var turns = task.Where(e => e.Kind == LedgerKinds.Turn).ToList();
            yield return (last.Ts, Row(Pipeline, "task", last, w =>
            {
                w.WriteString("task", task.Key);
                w.WriteString("status", Status(last.Outcome));
                w.WriteNumber("turns", turns.Count);
                w.WriteNumber("tokens_in", turns.Sum(t => t.TokensIn ?? 0));
                w.WriteNumber("tokens_out", turns.Sum(t => t.TokensOut ?? 0));
                if (turns.LastOrDefault(t => t.Model is not null)?.Model is { } tier) w.WriteString("tier", tier);
                w.WriteString("handover", handover.Id);
            }));
        }
    }

    /// Run statuses that end the run for good. "parked" waits on a needs-decision and "error" is
    /// the host failing; both resume under the same run id, so the handover stays open.
    private static readonly string[] Merged = ["merged", "auto_merge_enabled"];
    private static readonly string[] StillOpen = ["parked", "error"];

    private static (DateTimeOffset, RegistryEvent)? HandoverClosed(IReadOnlyList<LedgerEvent> run, HandoverRef handover)
    {
        var finished = run.LastOrDefault(e => e.Kind == LedgerKinds.Run);
        if (finished?.Outcome is null || StillOpen.Contains(finished.Outcome)) return null;
        var results = run
            .Where(e => e.Kind == LedgerKinds.Verify && e.Decision is not null && e.Payload is not null)
            .Select(e => (e, Command: CommandOf(e.Payload!)))
            .Where(x => x.Command is not null)
            .Select(x => new CheckResult(DecisionId.NewDecisionId(x.e.Decision!), x.Command!, x.e.Outcome == "passed"));
        var outcome = HandoverClosure.outcome(ListModule.OfSeq(handover.Decisions), Merged.Contains(finished.Outcome), ListModule.OfSeq(results));
        return (finished.Ts, Row("designc.jsonl", "handover_closed", finished, w =>
        {
            w.WriteString("handover", handover.Id);
            w.WriteString("outcome", outcome.IsDelivered ? "delivered" : "abandoned");
        }));
    }

    private static string? CommandOf(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        return Prop(doc.RootElement, "command")?.GetString();
    }

    /// TaskState case name -> designc task status.
    private static string Status(string state) => state switch
    {
        "Passed" => "passed",
        "Failed" => "failed",
        "Blocked" => "blocked",
        "Ready" or "Running" or "Verifying" => "running",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "not a TaskState case"),
    };

    /// Only needs-decisions that carry a registry id (ND-xxxx) are projected.
    private static RegistryEvent? NeedsDecision(LedgerEvent e)
    {
        if (e.Payload is null) return null;
        using var doc = JsonDocument.Parse(e.Payload);
        var p = doc.RootElement;
        if (Prop(p, "id")?.GetString() is not { } id || Prop(p, "question")?.GetString() is not { } question) return null;
        var element = Prop(p, "elements") is { ValueKind: JsonValueKind.Array } els && els.GetArrayLength() > 0 ? els[0].GetString() : e.Element;
        return Row(Pipeline, "needs_decision", e, w =>
        {
            w.WriteString("id", id);
            w.WriteString("question", question);
            w.WriteString("status", e.Outcome == "resolved" ? "resolved" : "open");
            if (e.TaskId is not null) w.WriteString("task", e.TaskId);
            if (element is not null) w.WriteString("element", element);
        }, w =>
        {
            // The options the agent offered; designc's resolution names one in payload.option.
            if (Prop(p, "options") is not { ValueKind: JsonValueKind.Array } options || options.GetArrayLength() == 0) return;
            w.WriteStartArray("options");
            foreach (var o in options.EnumerateArray())
            {
                w.WriteStartObject();
                foreach (var field in new[] { "id", "summary", "consequence" })
                    w.WriteString(field, Prop(o, field)?.GetString());
                w.WriteEndObject();
            }
            w.WriteEndArray();
            if (Prop(p, "recommended")?.GetString() is { } recommended) w.WriteString("recommended", recommended);
        });
    }

    private static RegistryEvent? Check(LedgerEvent e)
    {
        if (e.Payload is null || CommandOf(e.Payload) is not { } command) return null;
        using var doc = JsonDocument.Parse(e.Payload);
        var p = doc.RootElement;
        return Row(Checks, "check", e, w =>
        {
            w.WriteString("decision", e.Decision);
            w.WriteString("check", command);
            w.WriteString("outcome", e.Outcome == "passed" ? "pass" : "fail");
            w.WriteString("source", "pipeline");
            if (Prop(p, "ms") is { ValueKind: JsonValueKind.Number } ms) w.WriteNumber("ms", ms.GetInt64());
            if (Prop(p, "exitCode") is { ValueKind: JsonValueKind.Number } code) w.WriteNumber("exit_code", code.GetInt32());
        });
    }

    /// Envelope + kind fields in a fixed order, then event_id = hash of everything before it.
    private static RegistryEvent Row(string stream, string kind, LedgerEvent source, Action<Utf8JsonWriter> fields, Action<Utf8JsonWriter>? payload = null)
    {
        string Body(string? eventId)
        {
            using var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteString("kind", kind);
                fields(w);
                w.WriteString("by", By);
                w.WriteString("system", source.System);
                // designc's gate loader drops gate kinds carrying any envelope field beyond
                // system/session/task/element/payload, so the run and trace ids go under payload.
                w.WriteStartObject("payload");
                w.WriteString("run_id", source.RunId);
                if (source.TraceId is not null) w.WriteString("trace_id", source.TraceId);
                payload?.Invoke(w);
                w.WriteEndObject();
                w.WriteNumber("schema_version", 1);
                w.WriteNumber("ts", source.Ts.ToUnixTimeMilliseconds());
                if (eventId is not null) w.WriteString("event_id", eventId);
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        var id = "s2-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Body(null))))[..32];
        return new RegistryEvent(stream, id, Body(id));
    }

    private static JsonElement? Prop(JsonElement obj, string name)
    {
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }
}

/// Appends projected rows to {registry}/ledger/{stream}, skipping event_ids the stream already has.
public static class RegistryLedgerWriter
{
    public static async Task AppendAsync(string registryPath, IEnumerable<RegistryEvent> events, CancellationToken ct)
    {
        foreach (var stream in events.GroupBy(e => e.Stream))
        {
            var path = Path.Combine(registryPath, "ledger", stream.Key);
            var seen = await EventIdsAsync(path, ct);
            var lines = stream.Where(e => seen.Add(e.EventId)).Select(e => e.Line + "\n").ToList();
            if (lines.Count == 0) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var prefix = File.Exists(path) && new FileInfo(path).Length > 0 && !(await File.ReadAllTextAsync(path, ct)).EndsWith('\n') ? "\n" : "";
            await File.AppendAllTextAsync(path, prefix + string.Concat(lines), ct);
        }
    }

    private static async Task<HashSet<string>> EventIdsAsync(string path, CancellationToken ct)
    {
        var ids = new HashSet<string>();
        if (!File.Exists(path)) return ids;
        foreach (var line in await File.ReadAllLinesAsync(path, ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("event_id", out var id) && id.GetString() is { } s) ids.Add(s);
            }
            catch (JsonException) { } // designc owns the stream's validity; an unreadable row is not ours to judge
        }
        return ids;
    }
}
