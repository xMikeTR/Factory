namespace Factory.S2;

/// The `--repo` flag. One plain path (`--repo .`) is the single-repo form and behaves as it always has. Repeated
/// `Name=path` pairs (`--repo Api=../api --repo Ui=../ui`) name the repos of a split project; the first is the primary
/// (where `.factory/` state and policy live). The names are checked against the handover by the plan validator, not here.
public sealed record RepoMap(string? Primary, IReadOnlyDictionary<string, string> Named, IReadOnlyList<string> Errors)
{
    public static RepoMap FromArgs(string[] a) =>
        Parse(a.Select((x, i) => (x, i)).Where(t => t.x == "--repo" && t.i + 1 < a.Length).Select(t => a[t.i + 1]));

    public static RepoMap Parse(IEnumerable<string> specs)
    {
        var named = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new List<string>();
        string? plain = null;
        string? primary = null;
        foreach (var spec in specs)
        {
            var eq = spec.IndexOf('=');
            if (eq > 0 && IsName(spec[..eq]))
            {
                var path = spec[(eq + 1)..];
                if (path.Length == 0) { errors.Add($"--repo '{spec}' must be Name=path"); continue; }
                if (!named.TryAdd(spec[..eq], path)) errors.Add($"--repo '{spec[..eq]}' is given twice");
                primary ??= path;
            }
            else if (spec.EndsWith('=')) errors.Add($"--repo '{spec}' must be Name=path");
            else
            {
                if (plain is not null) errors.Add("--repo: more than one plain path; name each repo (--repo Api=<path> --repo Ui=<path>)");
                plain ??= spec;
                primary ??= spec;
            }
        }
        if (plain is not null && named.Count > 0) errors.Add($"--repo: '{plain}' has no name; give every repo a name or use one plain path");
        return new RepoMap(primary, named, errors);
    }

    private static bool IsName(string s) => s.Length > 0 && char.IsLetter(s[0]) && s.All(c => char.IsLetterOrDigit(c) || c is '_' or '-');
}
