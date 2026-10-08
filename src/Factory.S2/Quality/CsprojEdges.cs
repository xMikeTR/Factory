using System.Xml.Linq;
using Factory.Core;

namespace Factory.S2.Quality;

/// The C# adapter's dependency edges: each ProjectReference of a project file, as repo-relative paths.
public static class CsprojEdges
{
    public static IReadOnlyList<CodeEdge> Read(string projectPath, string xml)
    {
        var dir = Path.GetDirectoryName(projectPath)?.Replace('\\', '/') ?? "";
        return XDocument.Parse(xml).Descendants("ProjectReference")
            .Select(r => (string?)r.Attribute("Include"))
            .OfType<string>()
            .Select(include => new CodeEdge(projectPath, Normalise(dir, include.Replace('\\', '/'))))
            .ToList();
    }

    private static string Normalise(string dir, string relative)
    {
        var parts = new List<string>(dir.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var seg in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (seg != ".") parts.Add(seg);
        }
        return string.Join('/', parts);
    }
}
