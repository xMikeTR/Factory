using Factory.Core;

namespace Factory.Harness.Registry;

/// Read-only references (a legacy app being replaced): the handover names each by key and kind, the person running the
/// build binds a key to a folder with `--reference key=path`. Nothing here opens the folder; it only decides whether the
/// bindings are usable, so a run fails up front instead of an agent finding a missing reference mid-task.
public static class ReferenceBindings
{
    public sealed record Parsed(IReadOnlyDictionary<string, string> Bindings, IReadOnlyList<string> Errors);
    public sealed record Checked(IReadOnlyDictionary<string, string> Paths, IReadOnlyList<string> Errors);

    /// `key=path` per flag. The key ends at the first `=`, so a path may hold more.
    public static Parsed Parse(IEnumerable<string> specs)
    {
        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new List<string>();
        foreach (var spec in specs)
        {
            var eq = spec.IndexOf('=');
            if (eq <= 0 || eq == spec.Length - 1) { errors.Add($"--reference '{spec}' must be key=path"); continue; }
            var key = spec[..eq];
            if (!bindings.TryAdd(key, spec[(eq + 1)..])) errors.Add($"--reference '{key}' is given twice");
        }
        return new Parsed(bindings, errors);
    }

    /// The handover's keys against the bindings: every key bound, every binding a key, and each path an existing folder
    /// outside the repo (a reference inside the worktree would be writable through the ordinary tools).
    public static Checked Check(IEnumerable<Reference> declared, IReadOnlyDictionary<string, string> bindings, string repoPath)
    {
        var errors = new List<string>();
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var keys = declared.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        var repo = Real(repoPath);
        foreach (var key in keys.Where(k => !bindings.ContainsKey(k)).Order())
            errors.Add($"the handover has a read-only reference '{key}' but no path was given; pass --reference {key}=<folder>");
        foreach (var (key, path) in bindings.OrderBy(b => b.Key, StringComparer.Ordinal))
        {
            if (!keys.Contains(key)) { errors.Add($"--reference {key}: '{key}' is not in the handover's references"); continue; }
            var full = Path.GetFullPath(path);
            if (!Directory.Exists(full)) { errors.Add($"--reference {key}: {path} is not an existing folder"); continue; }
            var real = Real(full);
            if (real == repo || real.StartsWith(repo + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            { errors.Add($"--reference {key}: {path} is inside the repo; a reference must live outside the worktree"); continue; }
            paths[key] = full;
        }
        return new Checked(paths, errors);
    }

    /// Where the path ends up with every link on the way followed, a symlinked parent folder included.
    private static string Real(string path) =>
        Factory.Harness.ScopedWorkspace.RealPath(Path.GetFullPath(path)).TrimEnd(Path.DirectorySeparatorChar);
}
