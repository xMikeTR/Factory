using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Factory.Core;
using Microsoft.FSharp.Collections;

namespace Factory.Harness.Registry;

/// The handover a run's registry rows belong to, and the decisions whose checks decide how it closes.
public sealed record HandoverRef(string Id, IReadOnlyList<BoundDecision> Decisions, string? WorkItem = null)
{
    public static HandoverRef From(Handover h) =>
        new(h.Id, h.Decisions.ToList(), Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(h.WorkItem) ? h.WorkItem.Value : null);
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
        rows.AddRange(Attempts(run, handover));
        rows.AddRange(Tasks(run, handover));
        rows.AddRange(AsBuiltEdges(run));
        foreach (var e in run)
        {
            if (e.Kind == LedgerKinds.NeedsDecision && NeedsDecision(e) is { } nd) rows.Add((e.Ts, nd));
            else if (e.Kind == LedgerKinds.Verify && e.Decision is not null && Check(e) is { } check) rows.Add((e.Ts, check));
        }
        rows.AddRange(AcceptanceFailures(run, handover));
        if (HandoverClosed(run, handover) is { } closed) rows.Add(closed);
        // Stable: ties keep source order.
        return rows.OrderBy(r => r.Ts).Select(r => r.Row).ToList();
    }

    /// One row per task: its latest state, turn count and tokens so far.
    private static IEnumerable<(DateTimeOffset, RegistryEvent)> Tasks(IReadOnlyList<LedgerEvent> run, HandoverRef handover)
    {
        var groups = run.Where(e => e.TaskId is not null).GroupBy(e => e.TaskId!).ToList();
        // The planner works before any task exists: its run-level rows have no task, so share them over the tasks (the first takes the remainder).
        var planners = run.Where(e => e.Kind == LedgerKinds.Plan && e.TaskId is null).ToList();
        long planIn = planners.Sum(e => e.TokensIn ?? 0), planOut = planners.Sum(e => e.TokensOut ?? 0);
        var shared = groups.Count(g => g.Any(e => e.Kind == LedgerKinds.TaskEvent && e.Outcome is not null));
        var index = 0;
        foreach (var task in groups)
        {
            var last = task.LastOrDefault(e => e.Kind == LedgerKinds.TaskEvent);
            if (last?.Outcome is null) continue;
            var turns = task.Where(e => e.Kind == LedgerKinds.Turn).ToList();
            var share = index++;
            long ownIn = planIn / shared + (share == 0 ? planIn % shared : 0), ownOut = planOut / shared + (share == 0 ? planOut % shared : 0);
            yield return (last.Ts, Row(Pipeline, "task", last, w =>
            {
                w.WriteString("task", task.Key);
                w.WriteString("status", Status(last.Outcome));
                // The elements.json id (System/Container/Component) of the task's first element: designc's cost forecast keys on it.
                if (task.LastOrDefault(e => e.Kind == LedgerKinds.TaskEvent && e.Element is not null)?.Element is { } element) w.WriteString("element", element);
                w.WriteNumber("turns", turns.Count);
                w.WriteNumber("tokens_in", turns.Sum(t => t.TokensIn ?? 0) + ownIn);
                w.WriteNumber("tokens_out", turns.Sum(t => t.TokensOut ?? 0) + ownOut);
                if (turns.LastOrDefault(t => t.Model is not null)?.Model is { } tier) w.WriteString("tier", tier);
                WriteUsage(w, turns);
                WriteByRole(w, turns, ownIn, ownOut, share == 0 ? planners.Count : 0);
                w.WriteString("handover", handover.Id);
                w.WriteString("run", last.RunId);
            }));
        }
    }

    /// designc's task `usage`: one entry per tier and model, in the order first used, with cache_read and cache_write when any turn had them. A turn row with no model_id
    /// (logged before it existed, or a client that does not name its model) is named by its tier.
    private static void WriteUsage(Utf8JsonWriter w, IReadOnlyList<LedgerEvent> turns)
    {
        var groups = turns.Where(t => t.Model is not null)
            .GroupBy(t => (Tier: t.Model!, ModelId: ModelIdOf(t) ?? t.Model!)).ToList();
        if (groups.Count == 0) return;
        w.WriteStartArray("usage");
        foreach (var g in groups)
        {
            w.WriteStartObject();
            w.WriteString("model", g.Key.ModelId);
            w.WriteString("tier", g.Key.Tier);
            w.WriteNumber("turns", g.Count());
            w.WriteNumber("tokens_in", g.Sum(t => t.TokensIn ?? 0));
            w.WriteNumber("tokens_out", g.Sum(t => t.TokensOut ?? 0));
            long read = g.Sum(t => PayloadCount(t, "cache_read")), write = g.Sum(t => PayloadCount(t, "cache_write"));
            if (read + write > 0)
            {
                w.WriteNumber("cache_read", read);
                w.WriteNumber("cache_write", write);
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    /// designc's task `by_role`: tokens per role, in the order first seen. A turn names its role in its payload; a task-planner turn is known by its stage.
    /// The planner's share of the run (see Tasks) is its own entry, its turns counted once on the first task.
    private static void WriteByRole(Utf8JsonWriter w, IReadOnlyList<LedgerEvent> turns, long planIn, long planOut, int planTurns)
    {
        var groups = turns.GroupBy(RoleOf).ToList();
        var hasPlanner = planIn + planOut > 0;
        if (groups.Count == 0 && !hasPlanner) return;
        w.WriteStartArray("by_role");
        void Entry(string role, int count, long tin, long tout, int cold = 0)
        {
            w.WriteStartObject();
            w.WriteString("role", role);
            w.WriteNumber("turns", count);
            w.WriteNumber("tokens_in", tin);
            w.WriteNumber("tokens_out", tout);
            if (cold > 0) w.WriteNumber("cold", cold);
            w.WriteEndObject();
        }
        if (hasPlanner) Entry(Roles.Planner, planTurns, planIn, planOut);
        foreach (var g in groups) Entry(g.Key, g.Count(), g.Sum(t => t.TokensIn ?? 0), g.Sum(t => t.TokensOut ?? 0), g.Count(IsCold));
        w.WriteEndArray();
    }

    /// Reads one value out of a turn's payload object; `fallback` when there is no payload, it is not an object, or it is not JSON.
    private static T FromPayload<T>(LedgerEvent turn, Func<JsonElement, T> pick, T fallback)
    {
        if (turn.Payload is null) return fallback;
        try
        {
            using var doc = JsonDocument.Parse(turn.Payload);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? pick(doc.RootElement) : fallback;
        }
        catch (JsonException) { return fallback; }
    }

    private static bool IsCold(LedgerEvent turn) =>
        FromPayload(turn, p => p.TryGetProperty("cold", out var c) && c.ValueKind == JsonValueKind.True, false);

    private static string RoleOf(LedgerEvent turn) =>
        FromPayload<string?>(turn, p => p.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String && r.GetString() is { Length: > 0 } role ? role : null, null)
        ?? (turn.Stage == "task_plan" ? Roles.TaskPlanner : turn.Stage);

    private static long PayloadCount(LedgerEvent turn, string name) =>
        FromPayload(turn, p => p.TryGetProperty(name, out var n) && n.TryGetInt64(out var v) && v > 0 ? v : 0L, 0L);

    private static string? ModelIdOf(LedgerEvent turn) =>
        FromPayload<string?>(turn, p => p.TryGetProperty("model_id", out var m) && m.ValueKind == JsonValueKind.String && m.GetString() is { Length: > 0 } id ? id : null, null);

    /// One run_started per attempt and one run_ended per attempt that ended: at Finalize (a Run row with no
    /// task; a task's Run row is a rerun) or without it (RunAborted).
    private static IEnumerable<(DateTimeOffset, RegistryEvent)> Attempts(IReadOnlyList<LedgerEvent> run, HandoverRef handover)
    {
        foreach (var e in run)
        {
            if (e.Kind == LedgerKinds.RunStarted)
                yield return (e.Ts, Row(Pipeline, "run_started", e, w =>
                {
                    w.WriteString("run", e.RunId);
                    w.WriteString("handover", handover.Id);
                }));
            else if ((e.Kind == LedgerKinds.Run && e.TaskId is null || e.Kind == LedgerKinds.RunAborted) && e.Outcome is { } s2Status)
            {
                var (status, reason) = RunEnd(s2Status, e.Payload is null ? null : DetailOf(e.Payload));
                yield return (e.Ts, Row(Pipeline, "run_ended", e, w =>
                {
                    w.WriteString("run", e.RunId);
                    w.WriteString("handover", handover.Id);
                    w.WriteString("status", status);
                    if (reason is not null) w.WriteString("reason", reason);
                }));
            }
        }
    }

    /// An S2 run status -> designc's run_ended status and reason (required unless succeeded).
    private static (string Status, string? Reason) RunEnd(string s2Status, string? detail) => s2Status switch
    {
        "merged" or "auto_merge_enabled" => ("succeeded", null),
        "parked" or "cancelled" => (s2Status, string.IsNullOrWhiteSpace(detail) ? s2Status : detail),
        _ => ("failed", string.IsNullOrWhiteSpace(detail) ? s2Status : $"{s2Status}: {detail}"),
    };

    private static string? DetailOf(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        return Prop(doc.RootElement, "detail")?.GetString();
    }

    /// Debt the fitness ratchet let through: one row per element pair the code crosses with no relationship in the model,
    /// at its first sighting in the run (every verify attempt logs it again).
    private static IEnumerable<(DateTimeOffset, RegistryEvent)> AsBuiltEdges(IReadOnlyList<LedgerEvent> run)
    {
        var seen = new HashSet<(string, string)>();
        foreach (var e in run.Where(e => e.Kind == LedgerKinds.AsBuiltEdge && e.Payload is not null))
        {
            using var doc = JsonDocument.Parse(e.Payload!);
            if (Prop(doc.RootElement, "from")?.GetString() is not { } from || Prop(doc.RootElement, "to")?.GetString() is not { } to) continue;
            if (!seen.Add((from, to))) continue;
            yield return (e.Ts, Row(Pipeline, "asbuilt_edge", e, w =>
            {
                w.WriteString("from", from);
                w.WriteString("to", to);
            }));
        }
    }

    /// A failing acceptance spec, one row per (ac, element, commit). designc keys it on the work item, so a
    /// run whose handover names none projects nothing: the failure stays in S2's own ledger.
    private static IEnumerable<(DateTimeOffset, RegistryEvent)> AcceptanceFailures(IReadOnlyList<LedgerEvent> run, HandoverRef handover)
    {
        if (handover.WorkItem is not { } item) yield break;
        var seen = new HashSet<(string, string, string)>();
        foreach (var e in run.Where(e => e.Kind == LedgerKinds.UiAcceptanceFailed && e.Payload is not null && e.Element is not null))
        {
            using var doc = JsonDocument.Parse(e.Payload!);
            if (Prop(doc.RootElement, "ac")?.GetString() is not { } ac || Prop(doc.RootElement, "commit")?.GetString() is not { } commit) continue;
            if (!seen.Add((ac, e.Element!, commit))) continue;
            yield return (e.Ts, Row(Pipeline, "ui_acceptance_failed", e, w =>
            {
                w.WriteString("ac", ac);
                w.WriteString("element", e.Element);
                w.WriteString("task", item);
                w.WriteString("commit", commit);
            }));
        }
    }

    /// Run statuses that end the run for good. "parked" waits on a needs-decision and "error" is
    /// the host failing; both resume under the same run id, so the handover stays open.
    private static readonly string[] Merged = ["merged", "auto_merge_enabled"];

    private static (DateTimeOffset, RegistryEvent)? HandoverClosed(IReadOnlyList<LedgerEvent> run, HandoverRef handover)
    {
        var finished = run.LastOrDefault(e => e.Kind == LedgerKinds.Run);
        // Only a merge ends a handover: any other end can be resumed with --run-id, and abandoning is a person's call in designc.
        if (finished?.Outcome is null || !Merged.Contains(finished.Outcome)) return null;
        var results = run
            .Where(e => e.Kind == LedgerKinds.Verify && e.Decision is not null && e.Payload is not null)
            .Select(e => (e, Command: CommandOf(e.Payload!)))
            .Where(x => x.Command is not null)
            .Select(x => new CheckResult(DecisionId.NewDecisionId(x.e.Decision!), x.Command!, x.e.Outcome == "passed"));
        var outcome = HandoverClosure.outcome(ListModule.OfSeq(handover.Decisions), true, ListModule.OfSeq(results));
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
        "Ready" or "Running" or "Verifying" or "Reviewing" => "running",
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
