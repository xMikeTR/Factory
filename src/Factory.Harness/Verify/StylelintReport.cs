using System.Text.Json;
using Factory.Core;

namespace Factory.Harness.Verify;

/// Reads stylelint's JSON formatter output: an array of { source, warnings: [{ line, rule, text }] }.
/// Sources are made relative to the worktree so they can be matched against task `writes` globs.
public static class StylelintReport
{
    public static IReadOnlyList<LintViolation> Violations(string json, string worktree)
    {
        var found = new List<LintViolation>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return found;
        var root = Path.GetFullPath(worktree).TrimEnd('/') + "/";
        foreach (var file in doc.RootElement.EnumerateArray())
        {
            if (!file.TryGetProperty("source", out var s) || s.GetString() is not { } source) continue;
            var relative = source.StartsWith(root, StringComparison.Ordinal) ? source[root.Length..] : source;
            if (!file.TryGetProperty("warnings", out var warnings)) continue;
            foreach (var w in warnings.EnumerateArray())
                found.Add(new LintViolation(
                    relative.Replace('\\', '/'),
                    w.TryGetProperty("line", out var l) && l.TryGetInt32(out var line) ? line : 0,
                    w.TryGetProperty("rule", out var r) ? r.GetString() ?? "" : "",
                    w.TryGetProperty("text", out var t) ? t.GetString() ?? "" : ""));
        }
        return found.OrderBy(v => v.File, StringComparer.Ordinal).ThenBy(v => v.Line).ToList();
    }
}
