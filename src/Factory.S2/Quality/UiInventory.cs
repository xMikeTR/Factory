using System.Text.RegularExpressions;
using Factory.Core;
using FsCore = Factory.Core.Core;

namespace Factory.S2.Quality;

/// What the UI offers, by a heuristic scan of source text: data-testid attributes, React Router routes, UI test titles and which of
/// those tests are skipped. It sees names, not behaviour: a feature rewritten under the same test id looks unchanged.
public static class UiInventory
{
    public static readonly IReadOnlyList<PathPattern> DefaultGlobs =
        new[] { "**/*.ts", "**/*.tsx", "**/*.js", "**/*.jsx" }.Select(PathPatternModule.createOrFail).ToList();

    public static readonly IReadOnlyList<PathPattern> DefaultTestGlobs =
        new[] { "**/*.test.ts", "**/*.test.tsx", "**/*.test.js", "**/*.test.jsx", "**/*.spec.ts", "**/*.spec.tsx", "**/*.spec.js", "**/*.spec.jsx" }.Select(PathPatternModule.createOrFail).ToList();

    private static readonly string[] Vendored = ["node_modules", ".git", "bin", "obj", "dist", "build", "coverage"];

    private const string Quoted = """(?:"([^"]+)"|'([^']+)'|\{\s*(?:"([^"]+)"|'([^']+)'|`([^`$]+)`)\s*\})""";
    private static readonly Regex TestId = new(@"data-testid\s*=\s*" + Quoted);
    private static readonly Regex RouteElement = new(@"<Route\b(?:[^>{]|\{[^}]*\})*?\bpath\s*=\s*" + Quoted, RegexOptions.Singleline);
    private static readonly Regex RouteObject = new(@"\bpath\s*:\s*(?:""([^""]+)""|'([^']+)'|`([^`$]+)`)");
    private static readonly Regex RouterFile = new(@"createBrowserRouter|createHashRouter|createMemoryRouter|createRoutesFromElements|useRoutes|RouteObject");
    private static readonly Regex TestCall = new(@"\b(test|it|xtest|xit)(?:\.(skip|fixme|only))?\s*\(\s*(?:""([^""]*)""|'([^']*)'|`([^`$]*)`)");

    public static IReadOnlyList<UiItem> Scan(string root, IReadOnlyList<PathPattern> uiGlobs, IReadOnlyList<PathPattern> testGlobs)
    {
        var items = new List<UiItem>();
        foreach (var (rel, full) in Files(root))
        {
            var isTest = testGlobs.Any(g => FsCore.Matches(g, rel));
            if (!isTest && !uiGlobs.Any(g => FsCore.Matches(g, rel))) continue;
            string text;
            try { text = File.ReadAllText(full); } catch (IOException) { continue; }
            if (isTest) items.AddRange(Tests(text)); else items.AddRange(Markup(text));
        }
        return items.Distinct().ToList();
    }

    private static IEnumerable<(string Rel, string Full)> Files(string root)
    {
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var sub in Directory.EnumerateDirectories(dir).Where(d => !Vendored.Contains(System.IO.Path.GetFileName(d)) && !new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint))) pending.Push(sub);
            foreach (var file in Directory.EnumerateFiles(dir)) yield return (System.IO.Path.GetRelativePath(root, file).Replace('\\', '/'), file);
        }
    }

    private static string First(Match m) => m.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value;

    private static IEnumerable<UiItem> Markup(string text)
    {
        foreach (Match m in TestId.Matches(text)) yield return new UiItem(UiItemKind.TestId, First(m));
        foreach (Match m in RouteElement.Matches(text)) yield return new UiItem(UiItemKind.Route, First(m));
        if (RouterFile.IsMatch(text))
            foreach (Match m in RouteObject.Matches(text)) yield return new UiItem(UiItemKind.Route, First(m));
    }

    private static IEnumerable<UiItem> Tests(string text)
    {
        var calls = TestCall.Matches(text).Select(m => (Kind: m.Groups[1].Value, Mod: m.Groups[2].Value, Title: First(m, 3))).ToList();
        var focused = calls.Any(c => c.Mod == "only");
        foreach (var (kind, mod, title) in calls)
        {
            var skipped = kind.StartsWith('x') || mod is "skip" or "fixme" || (focused && mod != "only");
            yield return new UiItem(skipped ? UiItemKind.SkippedTest : UiItemKind.UiTest, title);
        }
    }

    private static string First(Match m, int from) => m.Groups.Cast<Group>().Skip(from).First(g => g.Success).Value;
}
