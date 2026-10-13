using Factory.Core;
using Factory.Harness.Policy;
using Xunit;

namespace Factory.Integration.Tests.Policy;

public class BudgetPolicyFileTests
{
    private static string RepoWith(string? yaml)
    {
        var repo = Path.Combine(Path.GetTempPath(), "s2-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repo, ".factory"));
        if (yaml is not null) File.WriteAllText(Path.Combine(repo, ".factory", "policy.yaml"), yaml);
        return repo;
    }

    [Fact]
    public void No_file_or_section_means_no_limit()
    {
        Assert.True(Microsoft.FSharp.Core.FSharpOption<long>.get_IsNone(BudgetPolicyFile.Load(RepoWith(null)).Default));
        var merge = BudgetPolicyFile.Load(RepoWith("merge:\n  enforcement: auto\n"));
        Assert.True(Microsoft.FSharp.Core.FSharpOption<long>.get_IsNone(merge.Default));
    }

    [Fact]
    public void The_section_sets_a_default_and_per_item_overrides_and_the_merge_policy_still_loads()
    {
        var repo = RepoWith("merge:\n  enforcement: auto\nbudget:\n  itemTokens: 2000\n  items:\n    W-BIG: 9000\n");

        var policy = BudgetPolicyFile.Load(repo);

        Assert.Equal(2000L, policy.Default.Value);
        Assert.Equal(9000L, policy.Overrides["W-BIG"]);
        Assert.True(MergePolicyFile.Load(repo, new Dictionary<string, string>()).Enforcement.IsAuto);
    }

    [Fact]
    public void A_negative_limit_is_rejected()
    {
        var e = Assert.Throws<FactoryRejectedException>(() => BudgetPolicyFile.Load(RepoWith("budget:\n  itemTokens: -1\n")));
        Assert.Contains(e.Errors, x => x.Contains("budget.itemTokens"));
    }
}
