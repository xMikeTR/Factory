using System.Text;
using System.Text.Json;

namespace Factory.S2.Quality;

/// A ledger row of the quality or mutation stage, as the report reads it.
public sealed record QualityRow(string Stage, string TaskId, string Outcome, string Payload);

/// The PR-body section for findings that report mode let through. A task's last row per stage decides, so a later
/// clean attempt clears an earlier finding (report mode ledgers "clean" when there is nothing to report).
public static class QualityReport
{
    public static string? Section(IEnumerable<QualityRow> rows)
    {
        var lines = new List<string>();
        foreach (var g in rows.GroupBy(r => (r.Stage, r.TaskId)).OrderBy(g => g.Key.TaskId, StringComparer.Ordinal).ThenBy(g => g.Key.Stage, StringComparer.Ordinal))
        {
            var last = g.Last();
            if (last.Outcome != "reported") continue;
            var field = last.Stage == "mutation" ? "survivors" : "violations";
            foreach (var l in JsonDocument.Parse(last.Payload).RootElement.GetProperty(field).EnumerateArray())
                lines.Add($"{g.Key.TaskId} ({last.Stage}) {l.GetString()!.TrimStart('-', ' ')}");
        }
        if (lines.Count == 0) return null;
        var sb = new StringBuilder("### Quality findings\nThe policy is in report mode, so these did not block:\n");
        foreach (var l in lines) sb.AppendLine($"- {l}");
        return sb.ToString();
    }
}
