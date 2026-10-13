using System.Text;
using System.Text.Json;

namespace Factory.Harness.Registry;

/// One variant's numbers, from the ledgers of the runs that used it.
public sealed record BrowserVariantStats(
    string Variant, int Tasks, int Passed, long TokensIn, long TokensOut, int ToolCalls, double WallSeconds, int AcceptanceFailures)
{
    /// Tokens (in + out) spent per task that reached Passed; null when none did.
    public double? TokensPerPassingTask => Passed == 0 ? null : (double)(TokensIn + TokensOut) / Passed;
    public double ToolCallsPerTask => Tasks == 0 ? 0 : (double)ToolCalls / Tasks;
    public double WallSecondsPerTask => Tasks == 0 ? 0 : WallSeconds / Tasks;
    /// Correctness: tasks passed over tasks run, and the acceptance failures integration verify blamed on them.
    public double PassRate => Tasks == 0 ? 0 : (double)Passed / Tasks;
}

/// The "measure, don't assume" comparison of ui-acceptance-playwright.md section 7: the same repro and self-check
/// tasks with the Playwright CLI and with MCP (or none), compared on tokens per passing task, tool calls per task,
/// wall time and correctness. Reads only the S2 ledger; the variant is what each acting turn logged as `browser`.
public static class BrowserToolComparison
{
    public static async Task<IReadOnlyList<BrowserVariantStats>> CompareAsync(ILedger ledger, IEnumerable<string> runIds, CancellationToken ct)
    {
        var acc = new Dictionary<string, Acc>(StringComparer.Ordinal);
        foreach (var runId in runIds)
        {
            var rows = new List<LedgerEvent>();
            await foreach (var e in ledger.ReadAsync(runId, ct)) rows.Add(e);

            // A run has one variant per task: whatever its first acting turn recorded.
            var variantOf = new Dictionary<string, string>(StringComparer.Ordinal);
            var calls = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var e in rows.Where(e => e.Kind == LedgerKinds.Turn && e.TaskId is not null && e.Stage == "act" && e.Payload is not null))
            {
                using var doc = JsonDocument.Parse(e.Payload!);
                if (doc.RootElement.TryGetProperty("browser", out var b) && b.GetString() is { } v) variantOf.TryAdd(e.TaskId!, v);
                if (doc.RootElement.TryGetProperty("tool_calls", out var c) && c.TryGetInt32(out var n))
                    calls[e.TaskId!] = calls.GetValueOrDefault(e.TaskId!) + n;
            }

            foreach (var (task, variant) in variantOf)
            {
                var mine = rows.Where(e => e.TaskId == task).ToList();
                var a = acc.TryGetValue(variant, out var x) ? x : acc[variant] = new Acc();
                a.Tasks++;
                if (mine.Any(e => e.Kind == LedgerKinds.TaskEvent && e.Outcome == "Passed")) a.Passed++;
                a.In += mine.Sum(e => e.TokensIn ?? 0);
                a.Out += mine.Sum(e => e.TokensOut ?? 0);
                a.Calls += calls.GetValueOrDefault(task);
                a.Wall += (mine.Max(e => e.Ts) - mine.Min(e => e.Ts)).TotalSeconds;
                a.Failures += mine.Count(e => e.Kind == LedgerKinds.UiAcceptanceFailed);
            }
        }
        return acc.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new BrowserVariantStats(kv.Key, kv.Value.Tasks, kv.Value.Passed, kv.Value.In, kv.Value.Out, kv.Value.Calls, kv.Value.Wall, kv.Value.Failures))
            .ToList();
    }

    public static string Render(IReadOnlyList<BrowserVariantStats> stats)
    {
        var sb = new StringBuilder();
        sb.AppendLine("variant  tasks  passed  tokens/passing task  tool calls/task  wall s/task  acceptance failures");
        foreach (var s in stats)
            sb.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0,-8} {1,5}  {2,6}  {3,19}  {4,15:0.0}  {5,11:0.0}  {6,19}",
                s.Variant, s.Tasks, s.Passed, s.TokensPerPassingTask is { } t ? t.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : "n/a",
                s.ToolCallsPerTask, s.WallSecondsPerTask, s.AcceptanceFailures));
        if (stats.Count < 2) sb.AppendLine("Only one variant in these runs: nothing to compare yet.");
        return sb.ToString();
    }

    private sealed class Acc { public int Tasks, Passed, Calls, Failures; public long In, Out; public double Wall; }
}
