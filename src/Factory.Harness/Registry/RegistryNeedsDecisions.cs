using System.Text.Json;
using System.Text.RegularExpressions;

namespace Factory.Harness.Registry;

/// A designc `needs_decision_resolved` row. Decision is the envelope's optional `decision`
/// (the decision the designer bound, if they named one); Note is payload.note.
public sealed record NeedsDecisionResolution(string Id, string? Decision, string? Note);

/// The needs-decision side of designc's registry ledger: which ND ids are taken, and which are resolved.
public sealed partial class RegistryNeedsDecisions(string registryPath)
{
    /// The next free ND-xxxx after every id in the registry ledger and `taken`.
    public async Task<string> NextIdAsync(IEnumerable<string> taken, CancellationToken ct)
    {
        var max = taken.Select(Number).DefaultIfEmpty(0).Max();
        await foreach (var row in RowsAsync(ct))
            if (Str(row, "kind") == "needs_decision" && Str(row, "id") is { } id) max = Math.Max(max, Number(id));
        return $"ND-{max + 1:D4}";
    }

    public async Task<IReadOnlyDictionary<string, NeedsDecisionResolution>> ResolutionsAsync(CancellationToken ct)
    {
        var resolved = new Dictionary<string, NeedsDecisionResolution>();
        await foreach (var row in RowsAsync(ct))
            if (Str(row, "kind") == "needs_decision_resolved" && Str(row, "id") is { } id)
                resolved[id] = new(id, Str(row, "decision"),
                    row.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.Object ? Str(p, "note") : null);
        return resolved;
    }

    private async IAsyncEnumerable<JsonElement> RowsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var dir = Path.Combine(registryPath, "ledger");
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.GetFiles(dir, "*.jsonl").Order())
            foreach (var line in await File.ReadAllLinesAsync(file, ct))
            {
                JsonElement row;
                try { row = JsonDocument.Parse(line).RootElement; }
                catch (JsonException) { continue; }
                if (row.ValueKind == JsonValueKind.Object) yield return row;
            }
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Number(string id) => NdId().Match(id) is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0;

    [GeneratedRegex(@"^ND-(\d+)$")]
    private static partial Regex NdId();
}
