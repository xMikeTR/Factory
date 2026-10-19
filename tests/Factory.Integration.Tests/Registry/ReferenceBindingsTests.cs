using Xunit;
using Factory.Core;
using Factory.Harness.Registry;

namespace Factory.Integration.Tests.Registry;

public sealed class ReferenceBindingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "s2-rb-" + Guid.NewGuid().ToString("N"));
    private string Repo => Path.Combine(_root, "target");
    private string Legacy => Path.Combine(_root, "legacy");

    public ReferenceBindingsTests()
    {
        Directory.CreateDirectory(Repo);
        Directory.CreateDirectory(Legacy);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static Reference Wpf => new("wpf", "legacy");

    [Fact]
    public void Flags_are_key_equals_path_and_a_malformed_one_is_refused()
    {
        var ok = ReferenceBindings.Parse(["wpf=/a/b", "other=c:/x=y"]);
        Assert.Equal(["/a/b", "c:/x=y"], [ok.Bindings["wpf"], ok.Bindings["other"]]);
        Assert.Empty(ok.Errors);

        Assert.NotEmpty(ReferenceBindings.Parse(["nokey"]).Errors);
        Assert.NotEmpty(ReferenceBindings.Parse(["=/a"]).Errors);
        Assert.NotEmpty(ReferenceBindings.Parse(["wpf="]).Errors);
        Assert.NotEmpty(ReferenceBindings.Parse(["wpf=/a", "wpf=/b"]).Errors);
    }

    [Fact]
    public void A_matching_existing_directory_outside_the_repo_is_resolved_to_its_full_path()
    {
        var r = ReferenceBindings.Check([Wpf], new Dictionary<string, string> { ["wpf"] = Legacy }, Repo);

        Assert.Empty(r.Errors);
        Assert.Equal(Path.GetFullPath(Legacy), r.Paths["wpf"]);
    }

    [Fact]
    public void A_handover_key_with_no_path_fails()
    {
        var r = ReferenceBindings.Check([Wpf], new Dictionary<string, string>(), Repo);

        Assert.Contains(r.Errors, e => e.Contains("wpf") && e.Contains("--reference"));
    }

    [Fact]
    public void A_path_for_an_unknown_key_fails()
    {
        var r = ReferenceBindings.Check([], new Dictionary<string, string> { ["wpf"] = Legacy }, Repo);

        Assert.Contains(r.Errors, e => e.Contains("wpf") && e.Contains("not in the handover"));
    }

    [Fact]
    public void A_path_reached_through_a_symlinked_folder_into_the_repo_is_still_inside_it()
    {
        Directory.CreateDirectory(Path.Combine(Repo, "refs"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "alias"), Repo);

        var r = ReferenceBindings.Check([Wpf], new Dictionary<string, string> { ["wpf"] = Path.Combine(_root, "alias", "refs") }, Repo);

        Assert.Contains(r.Errors, e => e.Contains("inside the repo"));
    }

    [Fact]
    public void A_missing_path_a_file_and_a_path_inside_the_repo_fail()
    {
        var file = Path.Combine(_root, "f.txt");
        File.WriteAllText(file, "x");
        var inside = Path.Combine(Repo, "legacy-copy");
        Directory.CreateDirectory(inside);

        Assert.NotEmpty(ReferenceBindings.Check([Wpf], new Dictionary<string, string> { ["wpf"] = Path.Combine(_root, "nope") }, Repo).Errors);
        Assert.NotEmpty(ReferenceBindings.Check([Wpf], new Dictionary<string, string> { ["wpf"] = file }, Repo).Errors);
        Assert.Contains(ReferenceBindings.Check([Wpf], new Dictionary<string, string> { ["wpf"] = inside }, Repo).Errors, e => e.Contains("inside the repo"));
        Assert.Contains(ReferenceBindings.Check([Wpf], new Dictionary<string, string> { ["wpf"] = Repo }, Repo).Errors, e => e.Contains("inside the repo"));
    }
}
