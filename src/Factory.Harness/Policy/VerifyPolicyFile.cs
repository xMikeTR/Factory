using Factory.Core;
using YamlDotNet.Serialization;

namespace Factory.Harness.Policy;

/// The repo's one source of truth for "is this good": the same npm scripts run by the editor hook, the git hook, CI and
/// here. Read from the base checkout like the rest of .factory/policy.yaml, so no task can change what judges it.
///
///   verify:
///     command: npm run verify
///     parity: npm run parity
///
/// `command` runs as part of every task's verify; `parity` is for the UI parity gate (phase 7c).
public sealed record VerifyConvention(string Command, string? Parity);

public static class VerifyPolicyFile
{
    private sealed class Doc { public Section? Verify { get; set; } }
    private sealed class Section { public string? Command { get; set; } public string? Parity { get; set; } }

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static VerifyConvention? Load(string repoPath)
    {
        var path = Path.Combine(repoPath, MergePolicyFile.RelativePath);
        if (PolicyYaml.Read<Doc>(Yaml, path)?.Verify is not { } s) return null;
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(s.Command)) errors.Add("verify.command is blank");
        if (s.Parity is not null && string.IsNullOrWhiteSpace(s.Parity)) errors.Add("verify.parity is blank");
        if (errors.Count > 0) throw new FactoryRejectedException($"{path} is invalid", errors.ToArray());
        return new VerifyConvention(s.Command!.Trim(), s.Parity?.Trim());
    }
}
