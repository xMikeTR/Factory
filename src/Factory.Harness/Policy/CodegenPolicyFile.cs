using Factory.Core;
using YamlDotNet.Serialization;

namespace Factory.Harness.Policy;

/// Which repos get generated C# contracts, from the primary repo's .factory/policy.yaml (the base checkout, so no task can change it):
///
///   codegen:
///     csharp: [Api, Worker]
///
/// Absent means the default (in a split run every repo that serves an operation; in a single-repo run, ".").
public static class CodegenPolicyFile
{
    private sealed class Doc { public Section? Codegen { get; set; } }
    private sealed class Section { public List<string>? Csharp { get; set; } }

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// null when the file or the `codegen.csharp` list is absent.
    public static IReadOnlyList<string>? Load(string repoPath)
    {
        var path = Path.Combine(repoPath, MergePolicyFile.RelativePath);
        if (PolicyYaml.Read<Doc>(Yaml, path)?.Codegen?.Csharp is not { } names) return null;
        var errors = Check(names);
        if (errors.Count > 0) throw new FactoryRejectedException($"{path} is invalid", errors.ToArray());
        return names.Select(n => n.Trim()).ToList();
    }

    private static List<string> Check(IReadOnlyList<string> names)
    {
        var errors = new List<string>();
        if (names.Any(string.IsNullOrWhiteSpace)) errors.Add("codegen.csharp has a blank repo name");
        foreach (var d in names.Where(n => !string.IsNullOrWhiteSpace(n)).GroupBy(n => n.Trim()).Where(g => g.Count() > 1))
            errors.Add($"codegen.csharp lists '{d.Key}' twice");
        return errors;
    }

    /// A name must be a run repo in a split run (`runRepos` non-empty); in a single-repo run only "." is meaningful.
    public static IReadOnlyList<string> Unknown(IReadOnlyList<string> names, IReadOnlyCollection<string> runRepos) =>
        runRepos.Count > 0
            ? names.Where(n => !runRepos.Contains(n)).Select(n => $"codegen.csharp names '{n}', which is not a repo of this run ({string.Join(", ", runRepos.Order(StringComparer.Ordinal))})").ToList()
            : names.Where(n => n != ".").Select(n => $"codegen.csharp names '{n}', but this is a single-repo run (use \".\")").ToList();
}
