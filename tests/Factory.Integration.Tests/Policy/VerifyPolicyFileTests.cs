using Factory.Core;
using Factory.Harness.Policy;
using Factory.S2;
using Xunit;

namespace Factory.Integration.Tests.Policy;

public class VerifyPolicyFileTests
{
    private static string RepoWith(string? yaml)
    {
        var repo = Path.Combine(Path.GetTempPath(), "s2-verify-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repo, ".factory"));
        if (yaml is not null) File.WriteAllText(Path.Combine(repo, ".factory", "policy.yaml"), yaml);
        return repo;
    }

    [Fact]
    public void No_verify_section_means_no_convention()
    {
        Assert.Null(VerifyPolicyFile.Load(RepoWith(null)));
        Assert.Null(VerifyPolicyFile.Load(RepoWith("npm:\n  rebuild: []\n")));
    }

    [Fact]
    public void A_repo_names_its_one_verify_and_parity_scripts()
    {
        var repo = RepoWith("verify:\n  command: npm run verify\n  parity: npm run parity\n");

        var v = VerifyPolicyFile.Load(repo)!;

        Assert.Equal("npm run verify", v.Command);
        Assert.Equal("npm run parity", v.Parity);
        Assert.Equal("npm run verify", FactorySettings.FromEnvironment(repo, null, null).Verify!.Command);
    }

    /// FactorySettings reads the whole file strictly: every section of policy.yaml must be known to the merge reader.
    [Fact]
    public void A_policy_with_routing_and_verify_sections_loads_as_settings()
    {
        var repo = RepoWith("routing:\n  canaryShare: 0.1\nverify:\n  command: npm run verify\n");

        Assert.Equal("npm run verify", FactorySettings.FromEnvironment(repo, null, null).Verify!.Command);
    }

    [Fact]
    public void Parity_is_optional_and_a_blank_command_is_rejected()
    {
        Assert.Null(VerifyPolicyFile.Load(RepoWith("verify:\n  command: npm run verify\n"))!.Parity);
        var e = Assert.Throws<FactoryRejectedException>(() => VerifyPolicyFile.Load(RepoWith("verify:\n  command: ' '\n")));
        Assert.Contains(e.Errors, x => x.Contains("verify.command"));
    }
}
