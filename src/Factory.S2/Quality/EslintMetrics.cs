using System.Text.Json;
using System.Text.RegularExpressions;
using Factory.Core;

namespace Factory.S2.Quality;

/// TypeScript metrics from `eslint --format json` run with the `complexity` rule at threshold 0, so it
/// reports every function: its name, its cyclomatic complexity and the lines it spans. ESLint runs in the
/// runner image; this only reads its report. Coverage is not joined yet (CRAP is judged only where it exists).
public static partial class EslintMetrics
{
    public static IReadOnlyList<FunctionMetrics> Parse(string json, string repoRoot)
    {
        var root = repoRoot.TrimEnd('/') + "/";
        using var doc = JsonDocument.Parse(json);
        var functions = new List<FunctionMetrics>();
        foreach (var file in doc.RootElement.EnumerateArray())
        {
            var path = file.GetProperty("filePath").GetString()!.Replace('\\', '/');
            if (path.StartsWith(root, StringComparison.Ordinal)) path = path[root.Length..];
            foreach (var m in file.GetProperty("messages").EnumerateArray())
            {
                if (m.TryGetProperty("ruleId", out var rule) && rule.GetString() != "complexity") continue;
                if (Complexity().Match(m.GetProperty("message").GetString()!) is not { Success: true } hit) continue;
                var line = m.GetProperty("line").GetInt32();
                var end = m.TryGetProperty("endLine", out var e) ? e.GetInt32() : line;
                var name = hit.Groups["name"].Success ? hit.Groups["name"].Value : "<anonymous>";
                functions.Add(new FunctionMetrics(path, name, line, int.Parse(hit.Groups["c"].Value), end - line + 1, null));
            }
        }
        return functions;
    }

    [GeneratedRegex(@"^(?:\w+ )*(?:function|method|arrow function|constructor|getter|setter)(?: '(?<name>[^']+)')? has a complexity of (?<c>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex Complexity();
}
