using Factory.Core;
using YamlDotNet.Serialization;

namespace Factory.Harness.Policy;

/// The API contract gate of a split project, tool-agnostic: S2 runs the commands, the repo decides what they are.
/// Read from the primary repo's .factory/policy.yaml like the rest, so no task can change what judges it.
///
///   contract:
///     breakingChangeCommand: oasdiff breaking origin/main:openapi.yaml openapi.yaml --fail-on ERR
///     clientCompileCommand: npm run gen:client && npm run build
///
/// `breakingChangeCommand` runs in the Api repo's integration checkout, `clientCompileCommand` in the Ui repo's
/// (generate the client from the pinned contract, then compile). Exit 0 passes. Either may be absent.
public sealed record ContractConvention(string? BreakingChangeCommand, string? ClientCompileCommand);

public static class ContractPolicyFile
{
    private sealed class Doc { public Section? Contract { get; set; } }
    private sealed class Section { public string? BreakingChangeCommand { get; set; } public string? ClientCompileCommand { get; set; } }

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static ContractConvention? Load(string repoPath)
    {
        var path = Path.Combine(repoPath, MergePolicyFile.RelativePath);
        if (PolicyYaml.Read<Doc>(Yaml, path)?.Contract is not { } s) return null;
        var errors = new List<string>();
        if (s.BreakingChangeCommand is not null && string.IsNullOrWhiteSpace(s.BreakingChangeCommand)) errors.Add("contract.breakingChangeCommand is blank");
        if (s.ClientCompileCommand is not null && string.IsNullOrWhiteSpace(s.ClientCompileCommand)) errors.Add("contract.clientCompileCommand is blank");
        if (s.BreakingChangeCommand is null && s.ClientCompileCommand is null) errors.Add("contract: name breakingChangeCommand, clientCompileCommand or both");
        if (errors.Count > 0) throw new FactoryRejectedException($"{path} is invalid", errors.ToArray());
        return new ContractConvention(s.BreakingChangeCommand?.Trim(), s.ClientCompileCommand?.Trim());
    }
}
