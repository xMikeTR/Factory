namespace Factory.Harness.Registry;

/// The files under tests/acceptance/** in a worktree, hashed the way Designc hashed them at freeze time.
public static class AcceptanceFiles
{
    public static IReadOnlyList<(string Path, string Hash)> Hashes(string worktree)
    {
        var root = Path.Combine(worktree, "tests", "acceptance");
        if (!Directory.Exists(root)) return [];
        return Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => (Path.GetRelativePath(worktree, f).Replace('\\', '/'), DesigncHandoverReader.Hash16(File.ReadAllText(f))))
            .OrderBy(x => x.Item1, StringComparer.Ordinal)
            .ToList();
    }
}
