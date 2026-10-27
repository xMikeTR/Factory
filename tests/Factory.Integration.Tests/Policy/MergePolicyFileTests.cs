using Factory.Core;
using Factory.Harness.Policy;
using Xunit;

namespace Factory.Integration.Tests.Policy;

using Core = Factory.Core.Core;

public class MergePolicyFileTests
{
    private static string RepoWith(string? yaml)
    {
        var repo = Path.Combine(Path.GetTempPath(), "s2-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repo, ".factory"));
        if (yaml is not null) File.WriteAllText(Path.Combine(repo, ".factory", "policy.yaml"), yaml);
        return repo;
    }

    private static readonly IReadOnlyDictionary<string, string> NoEnv = new Dictionary<string, string>();

    [Fact]
    public void No_policy_file_is_the_review_default()
    {
        var policy = MergePolicyFile.Load(RepoWith(null), NoEnv);

        Assert.Equal(Core.DefaultMergePolicy, policy);
        Assert.True(policy.Enforcement.IsReview);
    }

    [Fact]
    public void The_policy_file_sets_enforcement_approvers_and_thresholds()
    {
        var policy = MergePolicyFile.Load(RepoWith("""
            merge:
              enforcement: tiered
              approvers: [alice, bob]
              autoMergeMaxRisk: medium
              autoMergeMaxChangedLines: 120
            """), NoEnv);

        var tiered = Assert.IsType<MergeEnforcement.Tiered>(policy.Enforcement);
        Assert.Equal(["alice", "bob"], tiered.approvers);
        Assert.Equal(RiskLevel.Medium, policy.AutoMergeMaxRisk);
        Assert.Equal(120, policy.AutoMergeMaxChangedLines);
    }

    [Fact]
    public void The_environment_overrides_the_file()
    {
        var repo = RepoWith("merge:\n  enforcement: tiered\n  approvers: [alice]\n");

        var auto = MergePolicyFile.Load(repo, new Dictionary<string, string> { ["FACTORY_MERGE_ENFORCEMENT"] = "auto" });
        Assert.True(auto.Enforcement.IsAuto);

        var others = MergePolicyFile.Load(repo, new Dictionary<string, string> { ["FACTORY_MERGE_APPROVERS"] = "carol, dan" });
        Assert.Equal(["carol", "dan"], Assert.IsType<MergeEnforcement.Tiered>(others.Enforcement).approvers);
    }

    [Fact]
    public void An_unknown_value_is_rejected_not_defaulted()
    {
        var e = Assert.Throws<FactoryRejectedException>(() => MergePolicyFile.Load(RepoWith("merge:\n  enforcement: yolo\n"), NoEnv));
        Assert.Contains(e.Errors, x => x.Contains("enforcement"));
        Assert.Throws<FactoryRejectedException>(() => MergePolicyFile.Load(RepoWith("merge:\n  autoMergeMaxRisk: extreme\n"), NoEnv));
    }
}
