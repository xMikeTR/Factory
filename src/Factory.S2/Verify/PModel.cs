namespace Factory.S2.Verify;

/// A registry holds P sources (behaviour/*.p), but `p check` runs a compiled model. This copies the sources into a scratch
/// directory with a project file, so the registry itself stays clean; the factory then runs `p compile` there in the sandbox
/// and the decision checks run in the same directory.
public static class PModel
{
    public const string ProjectFile = "Model.pproj";

    /// The prepared directory, or null when the registry has no P sources.
    public static string? Prepare(string registryPath, string scratch)
    {
        var sources = Path.Combine(registryPath, "behaviour");
        var files = Directory.Exists(sources) ? Directory.GetFiles(sources, "*.p").OrderBy(f => f, StringComparer.Ordinal).ToList() : [];
        if (files.Count == 0) return null;

        if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        Directory.CreateDirectory(scratch);
        foreach (var f in files) File.Copy(f, Path.Combine(scratch, Path.GetFileName(f)));
        File.WriteAllText(Path.Combine(scratch, ProjectFile),
            "<Project>\n  <InputFiles>\n"
            + string.Concat(files.Select(f => $"    <PFile>{Path.GetFileName(f)}</PFile>\n"))
            + "  </InputFiles>\n  <ProjectName>Model</ProjectName>\n  <OutputDir>./PGenerated</OutputDir>\n</Project>\n");
        return scratch;
    }
}
