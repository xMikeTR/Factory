using System.Text.Json;
using Factory.Core;

namespace Factory.Harness.Routing;

/// 7c: the item-level parity verdict for a run, read from the registry after `factory-s2 parity`.
/// run -> handover id (runs/{run}/handover.json) -> items (registry `handover` rows) -> latest `parity_result` per scenario.
/// An item passes when every scenario's tier-1 outcome is `pass` and its visual tier (when present) is not `fail`.
/// No parity rows for the item means not judged yet: nothing is emitted, so the verify result stands.
internal static class RegistryParity
{
    public static long Bytes(string? registryPath)
    {
        var dir = registryPath is null ? null : Path.Combine(registryPath, "ledger");
        return dir is not null && Directory.Exists(dir) ? Directory.GetFiles(dir, "*.jsonl").Sum(f => new FileInfo(f).Length) : 0;
    }

    public static async Task<List<LearnEvent>> JudgeAsync(string stateDir, string? registryPath, List<LearnEvent> events, CancellationToken ct)
    {
        var judged = new List<LearnEvent>();
        var dir = registryPath is null ? null : Path.Combine(registryPath, "ledger");
        if (dir is null || !Directory.Exists(dir)) return judged;

        var itemsOf = new Dictionary<string, HashSet<string>>();        // handover id -> items
        var latest = new Dictionary<string, (long Ts, string Item, bool Passed)>();   // scenario -> latest
        foreach (var file in Directory.GetFiles(dir, "*.jsonl").Order())
            foreach (var line in await File.ReadAllLinesAsync(file, ct))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var r = doc.RootElement;
                    if (r.ValueKind != JsonValueKind.Object) continue;
                    switch (Str(r, "kind"))
                    {
                        case "handover" when Str(r, "item") is { } i && Str(r, "handover") is { } h:
                            if (!itemsOf.TryGetValue(h, out var set)) itemsOf[h] = set = [];
                            set.Add(i);
                            break;
                        case "parity_result" when Str(r, "scenario") is { } s && Str(r, "item") is { } item && Str(r, "outcome") is { } o:
                            var ts = r.TryGetProperty("ts", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : 0L;
                            if (latest.TryGetValue(s, out var prev) && prev.Ts > ts) break;
                            var visualFailed = r.TryGetProperty("visual", out var v) && v.ValueKind == JsonValueKind.Object && Str(v, "outcome") == "fail";
                            latest[s] = (ts, item, o == "pass" && !visualFailed);
                            break;
                    }
                }
                catch (JsonException) { /* a malformed row is skipped */ }
            }

        foreach (var runDir in Directory.Exists(Path.Combine(stateDir, "runs")) ? Directory.GetDirectories(Path.Combine(stateDir, "runs")) : [])
        {
            var run = Path.GetFileName(runDir);
            var handoverId = HandoverId(Path.Combine(runDir, "handover.json"));
            if (handoverId is null || !itemsOf.TryGetValue(handoverId, out var items)) continue;
            var results = latest.Values.Where(x => items.Contains(x.Item)).ToList();
            if (results.Count == 0) continue;
            var passed = results.All(x => x.Passed);
            foreach (var task in events.OfType<LearnEvent.Routed>().Select(e => e.Item.Task).Where(k => k.StartsWith(run + "/", StringComparison.Ordinal)).Distinct())
                judged.Add(LearnEvent.NewParityJudged(new ParityFact(task, passed)));
        }
        return judged;
    }

    private static string? HandoverId(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return Str(doc.RootElement, "id");
        }
        catch (Exception e) when (e is JsonException or IOException) { return null; }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
