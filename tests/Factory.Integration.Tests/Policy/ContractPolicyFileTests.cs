using Factory.Core;
using Factory.Harness.Policy;
using Factory.S2;
using Xunit;

namespace Factory.Integration.Tests.Policy;

public class ContractPolicyFileTests
{
    private static string RepoWith(string? yaml)
    {
        var repo = Path.Combine(Path.GetTempPath(), "s2-contract-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repo, ".factory"));
        if (yaml is not null) File.WriteAllText(Path.Combine(repo, ".factory", "policy.yaml"), yaml);
        return repo;
    }

    [Fact]
    public void No_contract_section_means_no_contract_gate()
    {
        Assert.Null(ContractPolicyFile.Load(RepoWith(null)));
        Assert.Null(FactorySettings.FromEnvironment(RepoWith("verify:\n  command: x\n"), null, null).Contract);
    }

    [Fact]
    public void A_repo_names_its_two_commands_and_settings_carry_them()
    {
        var repo = RepoWith("contract:\n  breakingChangeCommand: oasdiff breaking a b\n  clientCompileCommand: npm run gen && npm run build\n");

        var c = FactorySettings.FromEnvironment(repo, null, null).Contract!;

        Assert.Equal("oasdiff breaking a b", c.BreakingChangeCommand);
        Assert.Equal("npm run gen && npm run build", c.ClientCompileCommand);
    }

    [Fact]
    public void Either_command_alone_is_fine_but_an_empty_section_or_a_blank_command_is_rejected()
    {
        Assert.Null(ContractPolicyFile.Load(RepoWith("contract:\n  breakingChangeCommand: x\n"))!.ClientCompileCommand);
        Assert.NotEmpty(Assert.Throws<FactoryRejectedException>(() => ContractPolicyFile.Load(RepoWith("contract: {}\n"))).Errors);
        var e = Assert.Throws<FactoryRejectedException>(() => ContractPolicyFile.Load(RepoWith("contract:\n  clientCompileCommand: ' '\n")));
        Assert.Contains(e.Errors, x => x.Contains("clientCompileCommand"));
    }
}
