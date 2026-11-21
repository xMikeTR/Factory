using Factory.Core;
using YamlDotNet.Serialization;

namespace Factory.Harness.Policy;

/// What the codegen phase generates and how hand-written boundary code is judged, from the primary repo's .factory/policy.yaml
/// (the base checkout, so no task can change it):
///
///   codegen:
///     csharp: [Api, Worker]
///     typescript: [Ui]
///     handWritten: report   # off | report | block (default report)
///
/// An absent list means the default (C#: in a split run every repo that serves an operation, in a single-repo run "."; TypeScript:
/// repos that call an operation and have a package.json on the base branch).
public sealed record CodegenPolicy(IReadOnlyList<string>? Csharp, IReadOnlyList<string>? Typescript, QualityMode HandWritten)
{
    public static readonly CodegenPolicy Default = new(null, null, QualityMode.Report);
}

public static class CodegenPolicyFile
{
    private sealed class Doc { public Section? Codegen { get; set; } }
    private sealed class Section { public List<string>? Csharp { get; set; } public List<string>? Typescript { get; set; } public string? HandWritten { get; set; } }

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// The default policy when the file or the `codegen` section is absent.
    public static CodegenPolicy Load(string repoPath)
    {
        var path = Path.Combine(repoPath, MergePolicyFile.RelativePath);
        if (PolicyYaml.Read<Doc>(Yaml, path)?.Codegen is not { } section) return CodegenPolicy.Default;
        var errors = new List<string>();
        Check("codegen.csharp", section.Csharp, errors);
        Check("codegen.typescript", section.Typescript, errors);
        var mode = QualityMode.Report;
        if (section.HandWritten is { } word)
        {
            switch (word.Trim().ToLowerInvariant())
            {
                case "off": mode = QualityMode.Off; break;
                case "report": mode = QualityMode.Report; break;
                case "block": mode = QualityMode.Block; break;
                default: errors.Add($"codegen.handWritten: '{word}' is not off|report|block"); break;
            }
        }
        if (errors.Count > 0) throw new FactoryRejectedException($"{path} is invalid", errors.ToArray());
        return new CodegenPolicy(Trimmed(section.Csharp), Trimmed(section.Typescript), mode);
    }

    private static List<string>? Trimmed(List<string>? names) => names?.Select(n => n.Trim()).ToList();

    private static void Check(string key, IReadOnlyList<string>? names, List<string> errors)
    {
        if (names is null) return;
        if (names.Any(string.IsNullOrWhiteSpace)) errors.Add($"{key} has a blank repo name");
        foreach (var d in names.Where(n => !string.IsNullOrWhiteSpace(n)).GroupBy(n => n.Trim()).Where(g => g.Count() > 1))
            errors.Add($"{key} lists '{d.Key}' twice");
    }

    /// A name must be a run repo in a split run (`runRepos` non-empty); in a single-repo run only "." is meaningful.
    public static IReadOnlyList<string> Unknown(IReadOnlyList<string> names, IReadOnlyCollection<string> runRepos, string key = "codegen.csharp") =>
        runRepos.Count > 0
            ? names.Where(n => !runRepos.Contains(n)).Select(n => $"{key} names '{n}', which is not a repo of this run ({string.Join(", ", runRepos.Order(StringComparer.Ordinal))})").ToList()
            : names.Where(n => n != ".").Select(n => $"{key} names '{n}', but this is a single-repo run (use \".\")").ToList();
}
