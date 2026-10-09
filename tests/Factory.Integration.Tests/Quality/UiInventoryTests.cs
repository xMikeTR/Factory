using Factory.Core;
using Factory.S2.Quality;
using Xunit;

namespace Factory.Integration.Tests.Quality;

public class UiInventoryTests
{
    private static string Tree(params (string Path, string Text)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), "s2-ui-" + Guid.NewGuid().ToString("N"));
        foreach (var (path, text) in files)
        {
            var full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        return root;
    }

    private static IReadOnlyList<UiItem> Scan(string root) => UiInventory.Scan(root, UiInventory.DefaultGlobs, UiInventory.DefaultTestGlobs);

    private static IEnumerable<string> Ids(IEnumerable<UiItem> items, UiItemKind kind) => items.Where(i => i.Kind.Equals(kind)).Select(i => i.Id).Order();

    [Fact]
    public void Test_ids_are_read_from_jsx_attributes_in_any_quoting()
    {
        var items = Scan(Tree(("web/src/Report.tsx", """
            export const R = () => (<div>
              <button data-testid="export-btn">x</button>
              <input data-testid='name' /><p data-testid={"total"} /><p data-testid={`dyn-${id}`} />
            </div>);
            """)));

        Assert.Equal(["export-btn", "name", "total"], Ids(items, UiItemKind.TestId));
    }

    [Fact]
    public void React_Router_routes_come_from_Route_elements_and_route_objects()
    {
        var items = Scan(Tree(
            ("web/src/App.tsx", """<Routes><Route path="/reports" element={<R />} /><Route index element={<H />} /><Route element={<L />} path='/login'/></Routes>"""),
            ("web/src/router.ts", """export const router = createBrowserRouter([{ path: "/admin", element: x }]);"""),
            ("web/src/other.ts", """const cfg = { path: "/not-a-route" };""")));

        Assert.Equal(["/admin", "/login", "/reports"], Ids(items, UiItemKind.Route));
    }

    [Fact]
    public void Test_titles_are_listed_and_skipped_ones_are_marked_apart()
    {
        var items = Scan(Tree(("web/src/report.test.tsx", """
            test("exports a report", () => {});
            it('shows totals', () => {});
            test.skip("old thing", () => {});
            test.fixme("broken thing", () => {});
            xit("disabled", () => {});
            """)));

        Assert.Equal(["exports a report", "shows totals"], Ids(items, UiItemKind.UiTest));
        Assert.Equal(["broken thing", "disabled", "old thing"], Ids(items, UiItemKind.SkippedTest));
    }

    [Fact]
    public void Only_leaves_every_other_test_in_the_file_skipped()
    {
        var items = Scan(Tree(("web/a.spec.ts", "test.only(\"focus\", () => {});\ntest(\"other\", () => {});\n")));

        Assert.Equal(["focus"], Ids(items, UiItemKind.UiTest));
        Assert.Equal(["other"], Ids(items, UiItemKind.SkippedTest));
    }

    [Fact]
    public void Vendored_and_built_directories_are_not_scanned()
    {
        var items = Scan(Tree(("node_modules/x/y.tsx", "<a data-testid=\"vendored\"/>"), ("web/dist/bin/z.tsx", "<a data-testid=\"built\"/>"), ("web/ok.tsx", "<a data-testid=\"ok\"/>")));

        Assert.Equal(["ok"], Ids(items, UiItemKind.TestId));
    }
}
