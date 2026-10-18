using Factory.Core;
using YamlDotNet.Serialization;

namespace Factory.Harness.Policy;

/// The repo's own full suite, for a repo where `dotnet test` at the root does not run it (several projects and no
/// solution: MSB1003). Protected like the rest of .factory/policy.yaml: read from the base checkout, never a task
/// worktree, so no task can change what judges it. Absent = FACTORY_INTEGRATION_TEST_CMD, else `dotnet test`.
///
///   integration:
///     testCommand: dotnet test src/Game.Tests && dotnet test tools/Pipeline.Tests
public static class IntegrationPolicyFile
{
    private sealed class Doc { public Section? Integration { get; set; } }
    private sealed class Section { public string? TestCommand { get; set; } }

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static string? TestCommand(string repoPath)
    {
        var path = Path.Combine(repoPath, MergePolicyFile.RelativePath);
        var command = (File.Exists(path) ? Yaml.Deserialize<Doc?>(File.ReadAllText(path)) : null)?.Integration?.TestCommand;
        if (command is not null && string.IsNullOrWhiteSpace(command))
            throw new FactoryRejectedException($"{path} is invalid", ["integration.testCommand is blank"]);
        return command?.Trim();
    }
}
