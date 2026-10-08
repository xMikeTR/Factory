using System.Text.RegularExpressions;
using Factory.Core;
using YamlDotNet.Serialization;

namespace Factory.Harness.Policy;

/// Packages whose install script may run, offline, after `npm ci --ignore-scripts` (a native build such as esbuild).
/// Protected like the rest of .factory/policy.yaml: read from the base checkout, never a task worktree.
///
///   npm:
///     rebuild:
///       - esbuild
public sealed record NpmPolicy(IReadOnlyList<string> Rebuild);

public static partial class NpmPolicyFile
{
    private sealed class Doc { public Section? Npm { get; set; } }
    private sealed class Section { public List<string>? Rebuild { get; set; } }

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static NpmPolicy Load(string repoPath)
    {
        var path = Path.Combine(repoPath, MergePolicyFile.RelativePath);
        var rebuild = (File.Exists(path) ? Yaml.Deserialize<Doc?>(File.ReadAllText(path)) : null)?.Npm?.Rebuild ?? [];
        var bad = rebuild.Where(n => !PackageName().IsMatch(n)).Select(n => $"npm.rebuild: '{n}' is not an npm package name").ToArray();
        if (bad.Length > 0) throw new FactoryRejectedException($"{path} is invalid", bad);
        return new NpmPolicy(rebuild);
    }

    [GeneratedRegex(@"^(@[a-z0-9][a-z0-9._-]*/)?[a-z0-9][a-z0-9._-]*$")]
    private static partial Regex PackageName();
}
