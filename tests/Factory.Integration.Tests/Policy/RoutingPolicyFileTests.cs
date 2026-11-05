using Factory.Core;
using Factory.Harness.Policy;
using Xunit;

namespace Factory.Integration.Tests.Policy;

public class RoutingPolicyFileTests
{
    private static string RepoWith(string? yaml)
    {
        var repo = Path.Combine(Path.GetTempPath(), "s2-routing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repo, ".factory"));
        if (yaml is not null) File.WriteAllText(Path.Combine(repo, ".factory", "policy.yaml"), yaml);
        return repo;
    }

    [Fact]
    public void No_file_or_section_means_the_router_defaults()
    {
        foreach (var repo in new[] { RepoWith(null), RepoWith("merge:\n  enforcement: auto\n") })
        {
            var c = RoutingPolicyFile.Load(repo);

            Assert.True(c.Policy.MinTier.Invoke(RiskLevel.Low).IsSmall);
            Assert.True(c.Policy.MinTier.Invoke(RiskLevel.Medium).IsStandard);
            Assert.True(c.Policy.MinTier.Invoke(RiskLevel.High).IsStandard);
            Assert.Equal(Router.defaultPolicy.MinSamplesForMeans, c.Policy.MinSamplesForMeans);
            Assert.True(c.Spec.DataClass.IsInternal);
            Assert.Equal(4, c.Spec.SmallMaxWrites);
            Assert.Equal(10, c.Spec.MediumMaxWrites);
            Assert.True(c.Spec.Estimates.Invoke(Role.Implementer).Item1 > 0);
        }
    }

    [Fact]
    public void The_section_overrides_tiers_data_class_estimates_and_size_buckets()
    {
        var c = RoutingPolicyFile.Load(RepoWith("""
            routing:
              minTier: { low: standard, medium: standard, high: frontier }
              dataClass: regulated
              estimates:
                implementer: { in: 111, out: 22 }
              sizeBuckets: { s: 2, m: 6 }
            """));

        Assert.True(c.Policy.MinTier.Invoke(RiskLevel.Low).IsStandard);
        Assert.True(c.Policy.MinTier.Invoke(RiskLevel.High).IsFrontier);
        Assert.True(c.Spec.DataClass.IsRegulated);
        Assert.Equal(Tuple.Create(111, 22), c.Spec.Estimates.Invoke(Role.Implementer));
        Assert.True(c.Spec.Estimates.Invoke(Role.Reviewer).Item1 > 0); // unlisted roles keep their default
        Assert.Equal(2, c.Spec.SmallMaxWrites);
        Assert.Equal(6, c.Spec.MediumMaxWrites);
    }

    [Fact]
    public void Partial_minTier_keeps_the_default_for_the_rest()
    {
        var c = RoutingPolicyFile.Load(RepoWith("routing:\n  minTier: { high: frontier }\n"));

        Assert.True(c.Policy.MinTier.Invoke(RiskLevel.Low).IsSmall);
        Assert.True(c.Policy.MinTier.Invoke(RiskLevel.High).IsFrontier);
    }

    [Fact]
    public void Invalid_values_are_rejected_together()
    {
        var e = Assert.Throws<FactoryRejectedException>(() => RoutingPolicyFile.Load(RepoWith("""
            routing:
              minTier: { low: huge }
              dataClass: secret
              estimates:
                wizard: { in: 1, out: 1 }
                implementer: { in: -1, out: 1 }
              sizeBuckets: { s: 9, m: 3 }
            """)));

        Assert.Contains(e.Errors, x => x.Contains("huge"));
        Assert.Contains(e.Errors, x => x.Contains("secret"));
        Assert.Contains(e.Errors, x => x.Contains("wizard"));
        Assert.Contains(e.Errors, x => x.Contains("implementer"));
        Assert.Contains(e.Errors, x => x.Contains("sizeBuckets"));
    }
}
