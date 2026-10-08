using Factory.Core;
using Factory.Harness.Policy;
using Xunit;

namespace Factory.Integration.Tests.Policy;

public class QualityPolicyFileTests
{
    private static string RepoWith(string? yaml)
    {
        var repo = Path.Combine(Path.GetTempPath(), "s2-qpolicy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repo, ".factory"));
        if (yaml is not null) File.WriteAllText(Path.Combine(repo, ".factory", "policy.yaml"), yaml);
        return repo;
    }

    [Fact]
    public void No_quality_section_reports_with_the_default_thresholds_and_blocks_nothing()
    {
        var p = QualityPolicyFile.Load(RepoWith(null));

        Assert.Equal(QualityMode.Report, p.Mode);
        Assert.Equal(15, p.Thresholds.MaxCyclomatic);
    }

    [Fact]
    public void The_file_sets_the_mode_and_the_thresholds()
    {
        var p = QualityPolicyFile.Load(RepoWith("quality:\n  mode: block\n  maxCyclomatic: 8\n  maxCrap: 20\n  maxLines: 40\n"));

        Assert.Equal(QualityMode.Block, p.Mode);
        Assert.Equal((8, 20.0, 40), (p.Thresholds.MaxCyclomatic, p.Thresholds.MaxCrap, p.Thresholds.MaxLines));
    }

    [Fact]
    public void Coverage_is_off_unless_the_file_turns_it_on()
    {
        Assert.False(QualityPolicyFile.Load(RepoWith(null)).Coverage);
        Assert.True(QualityPolicyFile.Load(RepoWith("quality:\n  coverage: true\n")).Coverage);
    }

    [Fact]
    public void Mutation_is_off_unless_the_file_turns_it_on_and_wants_eighty_percent()
    {
        Assert.Equal(QualityMode.Off, QualityPolicyFile.Load(RepoWith(null)).Mutation!.Mode);

        var m = QualityPolicyFile.Load(RepoWith("quality:\n  mutation: block\n")).Mutation!;
        Assert.Equal((QualityMode.Block, 0.8), (m.Mode, m.Thresholds.MinScore));
        Assert.Equal(0.6, QualityPolicyFile.Load(RepoWith("quality:\n  mutation: report\n  mutationMinScore: 0.6\n")).Mutation!.Thresholds.MinScore);
    }

    [Fact]
    public void An_unknown_mutation_mode_or_a_score_outside_zero_to_one_is_rejected()
    {
        Assert.Throws<FactoryRejectedException>(() => QualityPolicyFile.Load(RepoWith("quality:\n  mutation: yolo\n")));
        Assert.Throws<FactoryRejectedException>(() => QualityPolicyFile.Load(RepoWith("quality:\n  mutationMinScore: 1.5\n")));
    }

    [Fact]
    public void An_unknown_mode_or_a_non_positive_limit_is_rejected()
    {
        Assert.Throws<FactoryRejectedException>(() => QualityPolicyFile.Load(RepoWith("quality:\n  mode: yolo\n")));
        Assert.Throws<FactoryRejectedException>(() => QualityPolicyFile.Load(RepoWith("quality:\n  maxCyclomatic: 0\n")));
    }

    [Fact]
    public void Merge_and_quality_share_the_file_without_disturbing_each_other()
    {
        var repo = RepoWith("merge:\n  enforcement: auto\nquality:\n  mode: off\n");

        Assert.Equal(QualityMode.Off, QualityPolicyFile.Load(repo).Mode);
        Assert.True(MergePolicyFile.Load(repo, new Dictionary<string, string>()).Enforcement.IsAuto);
    }
}
