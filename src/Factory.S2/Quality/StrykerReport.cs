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
        var names = TestNames(doc.RootElement);
        foreach (var file in doc.RootElement.GetProperty("files").EnumerateObject())
        {
            var path = file.Name.Replace('\\', '/');
            if (path.StartsWith(root, StringComparison.Ordinal)) path = path[root.Length..];
            foreach (var m in file.Value.GetProperty("mutants").EnumerateArray())
                mutants.Add(new Mutant(
                    m.GetProperty("id").GetString()!, path,
                    m.GetProperty("location").GetProperty("start").GetProperty("line").GetInt32(),
                    m.GetProperty("mutatorName").GetString()!,
                    StatusOf(m.GetProperty("status").GetString()!),
                    Tests(m, "coveredBy", names), Tests(m, "killedBy", names)));
        }
        return mutants;
    }

    // The report schema lists each test once under testFiles; a mutant refers to it by id. Both lists are optional.
    private static Dictionary<string, string> TestNames(JsonElement root)
    {
        var names = new Dictionary<string, string>();
        if (root.TryGetProperty("testFiles", out var files))
            foreach (var file in files.EnumerateObject())
                if (file.Value.TryGetProperty("tests", out var tests))
                    foreach (var t in tests.EnumerateArray())
                        if (t.TryGetProperty("id", out var id) && t.TryGetProperty("name", out var name)) names[id.GetString()!] = name.GetString()!;
        return names;
    }

    private static Microsoft.FSharp.Collections.FSharpList<string> Tests(JsonElement mutant, string property, Dictionary<string, string> names) =>
        Factory.Core.Core.ToList(mutant.TryGetProperty(property, out var ids) && ids.ValueKind == JsonValueKind.Array
            ? ids.EnumerateArray().Select(i => names.GetValueOrDefault(i.GetString()!, i.GetString()!)).ToList()
            : []);

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
