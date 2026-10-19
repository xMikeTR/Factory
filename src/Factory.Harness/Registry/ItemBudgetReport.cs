using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Factory.Core;

namespace Factory.Harness.Registry;

/// Per-work-item token spend against the limit in `.factory/policy.yaml` (`budget:`), reported to the registry as
/// Designc's `item_budget` rows. Spend is the tokens of every S2 ledger row in every run whose handover names the item
/// (a repair run carries the same item, so its tokens count). Designc only displays the reading; it never sets or
/// enforces the limit, and this report does not stop a run; `GateAsync` is what refuses a new task.
public static class ItemBudgetReport
{
    public const string RunStream = "budget";

    public static async Task<IReadOnlyDictionary<string, long>> SpendAsync(string stateDir, ILedger ledger, CancellationToken ct)
    {
        var spend = new Dictionary<string, long>(StringComparer.Ordinal);
        var runs = Path.Combine(stateDir, "runs");
        if (!Directory.Exists(runs)) return spend;
        foreach (var dir in Directory.GetDirectories(runs).Order(StringComparer.Ordinal))
        {
            var handover = Path.Combine(dir, "handover.json");
            if (!File.Exists(handover)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(await File.ReadAllTextAsync(handover, ct)); }
            catch (JsonException) { continue; } // a half-written handover.json names no item; one bad run must not hide every other item's spend
            using var _ = doc;
            if (!doc.RootElement.TryGetProperty("workItem", out var w) || w.ValueKind != JsonValueKind.String || w.GetString() is not { Length: > 0 } item) continue;
            long tokens = 0;
            await foreach (var e in ledger.ReadAsync(Path.GetFileName(dir), ct)) tokens += (e.TokensIn ?? 0) + (e.TokensOut ?? 0);
            spend[item] = spend.GetValueOrDefault(item) + tokens;
        }
        return spend;
    }

    /// Refuses a new task for an item already over its limit: returns the reason (and asks a person via one
    /// `item_awaiting_human` row, same reason = flagged once), or null when the task may start.
    public static async Task<string?> GateAsync(
        string stateDir, ILedger ledger, BudgetPolicy policy, string item, string? registryPath, DateTimeOffset now, CancellationToken ct)
    {
        var spent = (await SpendAsync(stateDir, ledger, ct)).GetValueOrDefault(item);
        if (ItemBudget.mayStart(policy, item, spent)) return null;
        var limit = ItemBudget.limitFor(policy, item).Value;
        var reason = $"{item} has spent {spent} of {limit} tokens: raise budget: in .factory/policy.yaml or decide in the digest";
        if (registryPath is not null)
        {
            await RunLedgerStream.AppendAsync(registryPath, RunStream, [AwaitingHumanRow(item, reason, now)], ct);
            await LedgerFold.RunAsync(registryPath, ct);
        }
        return reason;
    }

    private static RegistryEvent AwaitingHumanRow(string item, string reason, DateTimeOffset now)
    {
        var id = "s2-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"budget\n{item}\n{reason}")))[..32];
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("kind", "item_awaiting_human");
            w.WriteString("item", item);
            w.WriteString("reason", reason);
            w.WriteString("by", RegistryLedgerProjector.By);
            w.WriteString("system", "S2");
            w.WriteNumber("schema_version", 1);
            w.WriteNumber("ts", now.ToUnixTimeMilliseconds());
            w.WriteString("event_id", id);
            w.WriteEndObject();
        }
        return new RegistryEvent("pipeline.jsonl", id, Encoding.UTF8.GetString(buffer.ToArray()));
    }

    /// One reading per item that has a limit; items without one are not reported.
    public static IReadOnlyList<BudgetReading> Readings(BudgetPolicy policy, IReadOnlyDictionary<string, long> spend) =>
        spend.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => ItemBudget.read(policy, kv.Key, kv.Value))
            .Where(r => r is not null).Select(r => r!.Value).ToList();

    public static async Task<int> WriteAsync(string registryPath, IEnumerable<BudgetReading> readings, DateTimeOffset now, CancellationToken ct)
    {
        var rows = readings.Select(r => Row(r, now)).ToList();
        if (rows.Count == 0) return 0;
        await RunLedgerStream.AppendAsync(registryPath, RunStream, rows, ct);
        await LedgerFold.RunAsync(registryPath, ct);
        return rows.Count;
    }

    private static RegistryEvent Row(BudgetReading r, DateTimeOffset now)
    {
        var status = r.Status.IsExceeded ? "exceeded" : "within";
        string Body(string eventId)
        {
            using var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteString("kind", "item_budget");
                w.WriteString("item", r.Item);
                w.WriteString("status", status);
                w.WriteNumber("limit_tokens", r.Limit);
                w.WriteNumber("spent_tokens", r.Spent);
                if (r.Status.IsExceeded) w.WriteString("reason", $"spent {r.Spent} of {r.Limit} tokens");
                w.WriteString("by", RegistryLedgerProjector.By);
                w.WriteString("system", "S2");
                w.WriteNumber("schema_version", 1);
                w.WriteNumber("ts", now.ToUnixTimeMilliseconds());
                w.WriteString("event_id", eventId);
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        // The same reading is one row however often the report runs; a changed spend is a new row, and the latest wins.
        var id = "s2-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{r.Item}\n{status}\n{r.Limit}\n{r.Spent}")))[..32];
        return new RegistryEvent("pipeline.jsonl", id, Body(id));
    }

    public static string Render(IEnumerable<BudgetReading> readings) =>
        string.Concat(readings.Select(r =>
            $"  {(r.Status.IsExceeded ? "EXCEEDED" : "within  ")} {r.Item}: {r.Spent} of {r.Limit} tokens\n"));
}
