using System.Xml.Linq;
using Factory.Core;

namespace Factory.S2.Quality;

/// Joins Cobertura line coverage (coverlet's output) onto measured functions. A report's file names are
/// whatever the machine that ran it saw, so a function matches the report file that ends with its
/// repo-relative path. No coverable lines in range means no data (None), which `Quality.crap` counts as uncovered.
public static class Cobertura
{
    public static IReadOnlyList<FunctionMetrics> Join(IReadOnlyList<FunctionMetrics> functions, string xml) => Join(functions, [xml]);

    /// One report per test project: a line is covered if any of them ran it.
    public static IReadOnlyList<FunctionMetrics> Join(IReadOnlyList<FunctionMetrics> functions, IReadOnlyList<string> reports)
    {
        if (reports.Count == 0) return functions;
        var lines = reports.SelectMany(xml => XDocument.Parse(xml).Descendants("class")
            .SelectMany(c => c.Descendants("line").Select(l => (
                File: ((string?)c.Attribute("filename") ?? "").Replace('\\', '/'),
                Number: (int)l.Attribute("number")!,
                Hits: (int)l.Attribute("hits")!))))
            .ToList();

        return functions.Select(f =>
        {
            var inRange = lines.Where(l => l.File.EndsWith("/" + f.File, StringComparison.Ordinal) || l.File == f.File)
                .Where(l => l.Number >= f.Line && l.Number < f.Line + f.Lines)
                .GroupBy(l => l.Number).Select(g => g.Max(l => l.Hits)).ToList();
            return inRange.Count == 0
                ? f
                : new FunctionMetrics(f.File, f.Name, f.Line, f.Cyclomatic, f.Lines,
                    Microsoft.FSharp.Core.FSharpOption<double>.Some((double)inRange.Count(h => h > 0) / inRange.Count));
        }).ToList();
    }
}
