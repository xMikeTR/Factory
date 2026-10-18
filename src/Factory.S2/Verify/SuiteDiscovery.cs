using Factory.Core;

namespace Factory.S2.Verify;

using Core = Factory.Core.Core; // "Core" alone binds to the namespace Factory.Core from inside Factory.*

/// The full suite for a worktree: the named command, else what `Suite.discover` finds in the worktree's
/// solutions and projects. Read fresh each time, so a test project an earlier task added is run.
public static class SuiteDiscovery
{
    public static SuitePlan Plan(string? configured, string worktree)
    {
        if (configured is not null) return SuitePlan.NewCommand(configured);
        var solutions = new List<string>();
        var projects = new List<Tuple<string, string>>();
        foreach (var file in Files(worktree))
        {
            var relative = Path.GetRelativePath(worktree, file).Replace('\\', '/');
            switch (Path.GetExtension(file))
            {
                case ".sln" or ".slnx": solutions.Add(relative); break;
                case ".csproj" or ".fsproj": projects.Add(Tuple.Create(relative, File.ReadAllText(file))); break;
            }
        }
        return Suite.discover(Core.ToList(solutions), Core.ToList(projects));
    }

    private static IEnumerable<string> Files(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir)) yield return file;
        foreach (var sub in Directory.EnumerateDirectories(dir))
            if (!Suite.ignoredDirectory(Path.GetFileName(sub)))
                foreach (var file in Files(sub)) yield return file;
    }
}
