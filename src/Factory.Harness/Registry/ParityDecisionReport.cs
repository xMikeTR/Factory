using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Factory.Core;

namespace Factory.Harness.Registry;

/// Uncovered parity differences, put to a person: for each scenario whose latest `parity_result` (designc's recorder
/// writes it) is a fail, one open `needs_decision` row with `ParityDecision.raise`'s two options. could_not_run is the
/// environment, not a difference, so it raises nothing; a later run of the scenario supersedes an earlier fail.
public static class ParityDecisionReport
{
    public const string RunStream = "parity";

    /// The number of decisions raised; a (scenario, commit) already raised is not raised again.
    public static async Task<int> RaiseAsync(string registryPath, DateTimeOffset now, CancellationToken ct)
    {
        var (latest, raised) = await ReadAsync(registryPath, ct);
        var nds = new RegistryNeedsDecisions(registryPath);
        var taken = new List<string>();
        var rows = new List<RegistryEvent>();
        foreach (var r in latest.Values.Where(r => r.Outcome == "fail"))
        {
            var eventId = EventId(r.Scenario, r.Commit);
            if (raised.Contains(eventId)) continue;
            var id = await nds.NextIdAsync(taken, ct);
            taken.Add(id);
            rows.Add(Row(id, eventId, r, ParityDecision.raise(r.Scenario, r.Commit, Opt(r.Item), Microsoft.FSharp.Collections.ListModule.OfSeq(r.Failures)), now));
        }
        if (rows.Count == 0) return 0;
        await RunLedgerStream.AppendAsync(registryPath, RunStream, rows, ct);
        await LedgerFold.RunAsync(registryPath, ct);
        return rows.Count;
    }

    private sealed record Result(string Scenario, string Commit, string Outcome, string? Item, List<ParityFailure> Failures);

    /// The latest parity_result per scenario (ledger order), and the event ids of every row already in the ledger.
    private static async Task<(Dictionary<string, Result>, HashSet<string>)> ReadAsync(string registryPath, CancellationToken ct)
    {
        var latest = new Dictionary<string, (long Ts, Result R)>();
        var ids = new HashSet<string>();
        var dir = Path.Combine(registryPath, "ledger");
        if (!Directory.Exists(dir)) return ([], ids);
        foreach (var file in Directory.GetFiles(dir, "*.jsonl").Order())
            foreach (var line in await File.ReadAllLinesAsync(file, ct))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var r = doc.RootElement;
                    if (r.ValueKind != JsonValueKind.Object) continue;
                    if (Str(r, "event_id") is { } eid) ids.Add(eid);
                    if (Str(r, "kind") != "parity_result" || Str(r, "scenario") is not { } s || Str(r, "commit") is not { } c
                        || Str(r, "outcome") is not { } o) continue;
                    var ts = r.TryGetProperty("ts", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : 0L;
                    if (latest.TryGetValue(s, out var prev) && prev.Ts > ts) continue;
                    latest[s] = (ts, new Result(s, c, o, Str(r, "item"), Failures(r)));
                }
                catch (JsonException) { }
            }
        return (latest.ToDictionary(kv => kv.Key, kv => kv.Value.R), ids);
    }

    private static List<ParityFailure> Failures(JsonElement r) =>
        r.TryGetProperty("failures", out var fs) && fs.ValueKind == JsonValueKind.Array
            ? fs.EnumerateArray().Where(f => f.ValueKind == JsonValueKind.Object).Select(f => new ParityFailure(
                f.TryGetProperty("step", out var n) && n.TryGetInt32(out var step) ? step : 0,
                Str(f, "key") ?? "", Str(f, "kind") ?? "", Str(f, "msg") ?? "")).ToList()
            : [];

    private static string EventId(string scenario, string commit) =>
        "s2-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"parity-nd\n{scenario}\n{commit}")))[..32];

    private static RegistryEvent Row(string id, string eventId, Result r, NeedsDecision nd, DateTimeOffset now)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("kind", "needs_decision");
            w.WriteString("id", id);
            w.WriteString("question", nd.Question);
            w.WriteString("status", "open");
            w.WriteString("by", RegistryLedgerProjector.By);
            w.WriteString("system", "S2");
            w.WriteStartObject("payload");
            w.WriteStartArray("options");
            foreach (var o in nd.Options)
            {
                w.WriteStartObject();
                w.WriteString("id", o.Id);
                w.WriteString("summary", o.Summary);
                w.WriteString("consequence", o.Consequence);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            if (Opt(nd.Recommended) is { } rec) w.WriteString("recommended", rec);
            w.WriteStartObject("parity");
            w.WriteString("scenario", r.Scenario);
            w.WriteString("commit", r.Commit);
            if (r.Item is not null) w.WriteString("item", r.Item);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteNumber("schema_version", 1);
            w.WriteNumber("ts", now.ToUnixTimeMilliseconds());
            w.WriteString("event_id", eventId);
            w.WriteEndObject();
        }
        return new RegistryEvent("pipeline.jsonl", eventId, Encoding.UTF8.GetString(buffer.ToArray()));
    }

    private static Microsoft.FSharp.Core.FSharpOption<string> Opt(string? s) =>
        s is null ? Microsoft.FSharp.Core.FSharpOption<string>.None : Microsoft.FSharp.Core.FSharpOption<string>.Some(s);

    private static string? Opt(Microsoft.FSharp.Core.FSharpOption<string> o) =>
        Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(o) ? o.Value : null;

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
