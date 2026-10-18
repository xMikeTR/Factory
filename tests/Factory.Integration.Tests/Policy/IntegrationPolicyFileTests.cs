using Factory.Core;
using Factory.Harness.Policy;
using Factory.S2;
using Xunit;

namespace Factory.Integration.Tests.Policy;

public class IntegrationPolicyFileTests
{
    private static string RepoWith(string? yaml)
    {
        var repo = Path.Combine(Path.GetTempPath(), "s2-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repo, ".factory"));
        if (yaml is not null) File.WriteAllText(Path.Combine(repo, ".factory", "policy.yaml"), yaml);
        return repo;
    }

    [Fact]
    public void No_integration_section_leaves_the_command_to_the_environment()
    {
        Assert.Null(IntegrationPolicyFile.TestCommand(RepoWith(null)));
        Assert.Null(IntegrationPolicyFile.TestCommand(RepoWith("npm:\n  rebuild: []\n")));
    }

    /// 20261016-095826: several projects and no solution at the root, so `dotnet test` there was MSB1003.
    [Fact]
    public void A_repo_names_its_own_suite()
    {
        var repo = RepoWith("integration:\n  testCommand: dotnet test src/Game.Tests && dotnet test tools/Pipeline.Tests\n");

        Assert.Equal("dotnet test src/Game.Tests && dotnet test tools/Pipeline.Tests", IntegrationPolicyFile.TestCommand(repo));
        Assert.Equal("dotnet test src/Game.Tests && dotnet test tools/Pipeline.Tests",
            FactorySettings.FromEnvironment(repo, null, null).IntegrationTestCommand);
    }

    [Fact]
    public void A_blank_command_is_rejected()
    {
        Assert.Throws<FactoryRejectedException>(() => IntegrationPolicyFile.TestCommand(RepoWith("integration:\n  testCommand: '  '\n")));
    }
}
