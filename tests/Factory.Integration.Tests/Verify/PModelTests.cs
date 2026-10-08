using Factory.S2.Verify;
using Xunit;

namespace Factory.Integration.Tests.Verify;

/// A registry holds P sources, not a project: `p check` needs the compiled model, so the factory builds one in a scratch directory.
public class PModelTests
{
    private static string Dir() => Directory.CreateTempSubdirectory("pmodel").FullName;

    [Fact]
    public void Every_p_source_in_behaviour_is_copied_and_listed_in_a_project_file()
    {
        var registry = Dir();
        Directory.CreateDirectory(Path.Combine(registry, "behaviour"));
        File.WriteAllText(Path.Combine(registry, "behaviour/a.p"), "// a");
        File.WriteAllText(Path.Combine(registry, "behaviour/b.p"), "// b");
        File.WriteAllText(Path.Combine(registry, "behaviour/notes.md"), "not a model");
        var scratch = Path.Combine(Dir(), "p-model");

        var prepared = PModel.Prepare(registry, scratch);

        Assert.Equal(scratch, prepared);
        Assert.True(File.Exists(Path.Combine(scratch, "a.p")) && File.Exists(Path.Combine(scratch, "b.p")));
        Assert.False(File.Exists(Path.Combine(scratch, "notes.md")));
        var project = File.ReadAllText(Path.Combine(scratch, PModel.ProjectFile));
        Assert.Contains("<PFile>a.p</PFile>", project);
        Assert.Contains("<PFile>b.p</PFile>", project);
    }

    [Fact]
    public void A_registry_without_p_sources_has_nothing_to_compile()
    {
        Assert.Null(PModel.Prepare(Dir(), Path.Combine(Dir(), "p-model")));
    }

    [Fact]
    public void Preparing_again_starts_from_a_clean_directory()
    {
        var registry = Dir();
        Directory.CreateDirectory(Path.Combine(registry, "behaviour"));
        File.WriteAllText(Path.Combine(registry, "behaviour/a.p"), "// a");
        var scratch = Path.Combine(Dir(), "p-model");
        Directory.CreateDirectory(scratch);
        File.WriteAllText(Path.Combine(scratch, "stale.p"), "// old");

        PModel.Prepare(registry, scratch);

        Assert.False(File.Exists(Path.Combine(scratch, "stale.p")));
    }
}
