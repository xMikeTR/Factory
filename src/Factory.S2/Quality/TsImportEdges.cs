using System.Text.Json;
using Factory.Core;
using Factory.Harness.Sandbox;

namespace Factory.S2.Quality;

/// Import edges between the repo's TypeScript/JavaScript files, read from dependency-cruiser (in the runner image). Packages,
/// node built-ins and imports it could not resolve are not edges. No report (tool missing or crashed) reads as no edges.
public static class TsImportEdges
{
    private const string Command = "depcruise --no-config --output-type json --ts-pre-compilation-deps "
        + "$([ -f tsconfig.json ] && echo --ts-config tsconfig.json) --exclude '(^|/)(node_modules|dist|build|coverage|bin|obj|\\.git)/' .";

    private static readonly System.Text.RegularExpressions.Regex Report = new(@"\{\s*""modules""");

    public static async Task<IReadOnlyList<CodeEdge>> ReadAsync(ICommandSandbox sandbox, string worktree, CancellationToken ct)
    {
        var r = await sandbox.RunAsync(Command, worktree, TimeSpan.FromMinutes(3), new Dictionary<string, string>(), ct);
        var start = Report.Match(r.Output);
        if (!start.Success) return [];
        try { return Parse(r.Output[start.Index..]); }
        catch (JsonException) { return []; }
    }

    public static IReadOnlyList<CodeEdge> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
        var edges = new List<CodeEdge>();
        if (!doc.RootElement.TryGetProperty("modules", out var modules)) return edges;
        foreach (var m in modules.EnumerateArray())
        {
            var source = m.GetProperty("source").GetString()!;
            if (!m.TryGetProperty("dependencies", out var deps)) continue;
            foreach (var d in deps.EnumerateArray())
            {
                var resolved = d.GetProperty("resolved").GetString()!;
                if (d.GetProperty("coreModule").GetBoolean() || d.GetProperty("couldNotResolve").GetBoolean()
                    || resolved.StartsWith("node_modules/") || resolved.Contains("/node_modules/")) continue;
                edges.Add(new CodeEdge(source, resolved));
            }
        }
        return edges.Distinct().ToList();
    }
}
