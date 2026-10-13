using System.Text;
using System.Text.Json;
using Factory.Harness;

namespace Factory.S2;

/// repair.json: why a repair run exists. A run that ended `integration_failed` on frozen-spec or
/// token-lint failures is repaired by a new run cut from the failed integration branch that re-runs
/// only the tasks those failures blame; every other task is carried (its work is already on base).
/// Feedback maps a task id to what its brief must say.
public sealed record RepairFile(string Of, IReadOnlyDictionary<string, string> Feedback);

public static class Repair
{
    private const int TraceChars = 2000;

    /// The blamed tasks and what failed for each, read from the failed run's ledger; null when
    /// the run recorded no failure that names a task (nothing to repair).
    public static async Task<RepairFile?> FromFailedRunAsync(FactoryServices svc, string failedRunId, CancellationToken ct)
    {
        var lines = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        await foreach (var e in svc.Ledger.ReadAsync(failedRunId, ct))
        {
            if (e.TaskId is null || e.Payload is null || e.Outcome != "failed") continue;
            using var doc = JsonDocument.Parse(e.Payload);
            string S(string name) => doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            string? line = e.Kind switch
            {
                LedgerKinds.UiAcceptanceFailed =>
                    $"Frozen acceptance spec {S("spec")} (acceptance {S("ac")}{(e.Element is null ? "" : $" on element {e.Element}")}) failed on integration at {S("commit")}."
                    + (S("trace") is { Length: > 0 } t ? "\nTrace:\n" + (t.Length > TraceChars ? t[..TraceChars] + " …" : t) : ""),
                LedgerKinds.TokenLintViolation =>
                    $"Raw value where a design token belongs: {S("file")}:{doc.RootElement.GetProperty("line")} {S("rule")}: {S("text")}",
                _ => null,
            };
            if (line is null) continue;
            if (!lines.TryGetValue(e.TaskId, out var list)) lines[e.TaskId] = list = [];
            if (!list.Contains(line)) list.Add(line);
        }
        if (lines.Count == 0) return null;
        return new RepairFile(failedRunId, lines.ToDictionary(kv => kv.Key, kv => Brief(kv.Value)));
    }

    private static string Brief(List<string> lines)
    {
        var sb = new StringBuilder("Your earlier work merged and the integrated branch then failed checks you cannot edit. "
            + "Change only what your task writes so these pass; do not touch the frozen specs.\n");
        foreach (var l in lines) sb.AppendLine().AppendLine("- " + l);
        return sb.ToString().TrimEnd();
    }

    public static string? Feedback(FactorySettings s, string runId, string taskId)
    {
        var path = s.RepairPath(runId);
        if (!File.Exists(path)) return null;
        var file = JsonSerializer.Deserialize<RepairFile>(File.ReadAllText(path));
        return file?.Feedback.GetValueOrDefault(taskId);
    }
}
