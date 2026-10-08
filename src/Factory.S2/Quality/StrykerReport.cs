using System.Text.Json;
using Factory.Core;

namespace Factory.S2.Quality;

/// Reads Stryker's mutation-testing-elements JSON report into neutral mutants. Files are made
/// repo-relative against the root the tool ran in. A mutant that crashed the run (RuntimeError) or
/// was never run (Pending, Ignored) says nothing about the tests, so it is Ignored.
public static class StrykerReport
{
    public static IReadOnlyList<Mutant> Parse(string json, string repoRoot)
    {
        var root = repoRoot.TrimEnd('/') + "/";
        using var doc = JsonDocument.Parse(json);
        var mutants = new List<Mutant>();
        foreach (var file in doc.RootElement.GetProperty("files").EnumerateObject())
        {
            var path = file.Name.Replace('\\', '/');
            if (path.StartsWith(root, StringComparison.Ordinal)) path = path[root.Length..];
            foreach (var m in file.Value.GetProperty("mutants").EnumerateArray())
                mutants.Add(new Mutant(
                    m.GetProperty("id").GetString()!, path,
                    m.GetProperty("location").GetProperty("start").GetProperty("line").GetInt32(),
                    m.GetProperty("mutatorName").GetString()!,
                    StatusOf(m.GetProperty("status").GetString()!)));
        }
        return mutants;
    }

    private static MutantStatus StatusOf(string status) => status switch
    {
        "Killed" => MutantStatus.Killed,
        "Survived" => MutantStatus.Survived,
        "Timeout" => MutantStatus.Timeout,
        "NoCoverage" => MutantStatus.NoCoverage,
        "CompileError" => MutantStatus.CompileError,
        _ => MutantStatus.Ignored,
    };
}
