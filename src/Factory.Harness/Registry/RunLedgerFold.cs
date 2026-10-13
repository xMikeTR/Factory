using System.Text.Json;

namespace Factory.Harness.Registry;

/// Where each designc kind S2 can write lives. Mirrors designc's ledger_schema.json `stream`; the
/// contract tests compare it with the copied fixture so a schema change fails loudly.
public static class RegistryStreams
{
    private static readonly IReadOnlyDictionary<string, string> ByKind = new Dictionary<string, string>
    {
        ["task"] = "pipeline.jsonl",
        ["needs_decision"] = "pipeline.jsonl",
        ["asbuilt_edge"] = "pipeline.jsonl",
        ["false_positive"] = "pipeline.jsonl",
        ["item_awaiting_human"] = "pipeline.jsonl",
        ["item_conflict_deferred"] = "pipeline.jsonl",
        ["wip_limit_changed"] = "pipeline.jsonl",
        ["ui_acceptance_failed"] = "pipeline.jsonl",
        ["check"] = "checks.jsonl",
        ["item_gate"] = "checks.jsonl",
        ["handover_closed"] = "designc.jsonl",
        ["escape"] = "designc.jsonl",
    };

    public static IReadOnlyCollection<string> Kinds => ByKind.Keys.ToList();
    public static string? Of(string kind) => ByKind.GetValueOrDefault(kind);
}

/// A run's own registry stream, {registry}/ledger/runs/{run_id}.jsonl. Each run appends only to its
/// own file, so two concurrent runs never write the same stream; LedgerFold merges them.
public static class RunLedgerStream
{
    public static string PathOf(string registryPath, string runId) =>
        Path.Combine(registryPath, "ledger", "runs", SafeName(runId) + ".jsonl");

    public static async Task AppendAsync(string registryPath, string runId, IEnumerable<RegistryEvent> events, CancellationToken ct)
    {
        var path = PathOf(registryPath, runId);
        var seen = await RunFileIdsAsync(path, ct);
        var lines = events.Where(e => seen.Add(e.EventId)).Select(e => e.Line + "\n").ToList();
        if (lines.Count == 0) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.AppendAllTextAsync(path, string.Concat(lines), ct);
    }

    internal static async Task<HashSet<string>> RunFileIdsAsync(string path, CancellationToken ct)
    {
        var ids = new HashSet<string>();
        if (!File.Exists(path)) return ids;
        foreach (var line in await File.ReadAllLinesAsync(path, ct))
            if (Parse(line) is { } row) ids.Add(row.EventId);
        return ids;
    }

    internal static RegistryEvent? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("event_id", out var id) || id.GetString() is not { } eventId
                || !root.TryGetProperty("kind", out var kind) || kind.GetString() is not { } k
                || RegistryStreams.Of(k) is not { } stream) return null;
            return new RegistryEvent(stream, eventId, line);
        }
        catch (JsonException) { return null; }
    }

    private static string SafeName(string runId) =>
        string.Concat(runId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
}

public sealed record FoldReport(int Streams, int Appended, int Unreadable);

/// The single writer of the main streams. Holds {registry}/ledger/.fold.lock while it reads every
/// run stream and appends the rows the main streams have not seen, so concurrent folds serialise and
/// folding twice appends nothing.
public static class LedgerFold
{
    public static async Task<FoldReport> RunAsync(string registryPath, CancellationToken ct)
    {
        var ledger = Path.Combine(registryPath, "ledger");
        var runs = Path.Combine(ledger, "runs");
        if (!Directory.Exists(runs)) return new FoldReport(0, 0, 0);

        await using var gate = await AcquireAsync(Path.Combine(ledger, ".fold.lock"), ct);
        var files = Directory.GetFiles(runs, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal).ToList();
        var rows = new List<RegistryEvent>();
        var unreadable = 0;
        foreach (var file in files)
            foreach (var line in await File.ReadAllLinesAsync(file, ct))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (RunLedgerStream.Parse(line) is { } row) rows.Add(row);
                else unreadable++;
            }

        var before = Count(ledger);
        await RegistryLedgerWriter.AppendAsync(registryPath, rows, ct);
        return new FoldReport(files.Count, Count(ledger) - before, unreadable);
    }

    private static int Count(string ledger) =>
        RegistryStreams.Kinds.Select(RegistryStreams.Of).Distinct()
            .Select(s => Path.Combine(ledger, s!))
            .Where(File.Exists).Sum(p => File.ReadLines(p).Count(l => !string.IsNullOrWhiteSpace(l)));

    private static async Task<FileStream> AcquireAsync(string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(25, ct); }
        }
    }
}
