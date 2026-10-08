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
    public void Forbidden_dependencies_are_read_from_the_quality_section_and_a_bad_glob_is_rejected()
    {
        Assert.Empty(QualityPolicyFile.Load(RepoWith(null)).Forbid);

        var f = Assert.Single(QualityPolicyFile.Load(RepoWith(
            "quality:\n  forbid:\n    - from: src/Domain/**\n      to: src/Infra/**\n      why: domain is pure\n")).Forbid);
        Assert.Equal(("src/Domain/**", "src/Infra/**", "domain is pure"), (f.From.Value, f.To.Value, f.Why));

        Assert.Throws<FactoryRejectedException>(() => QualityPolicyFile.Load(RepoWith("quality:\n  forbid:\n    - from: src/Domain/**\n")));
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

    [Fact]
    public void Commands_marked_flaky_are_read_and_default_to_none()
    {
        Assert.Empty(QualityPolicyFile.Load(RepoWith("quality:\n  mode: report\n")).Flaky);
        Assert.Equal(["dotnet test --filter Slow"], QualityPolicyFile.Load(RepoWith("quality:\n  flaky:\n    - dotnet test --filter Slow\n")).Flaky);
    }

    [Fact]
    public void Forbidden_namespaces_use_dotted_globs_and_are_kept_apart_from_project_rules()
    {
        var policy = QualityPolicyFile.Load(RepoWith(
            "quality:\n  forbidNamespaces:\n    - from: Pricing.Domain.**\n      to: Pricing.Infra.**\n      why: domain is pure\n"));

        var rule = Assert.Single(policy.ForbidNamespaces);
        Assert.Equal(("Pricing/Domain/**", "Pricing/Infra/**", "domain is pure"), (rule.From.Value, rule.To.Value, rule.Why));
        Assert.Empty(policy.Forbid);
        Assert.Throws<FactoryRejectedException>(() => QualityPolicyFile.Load(RepoWith("quality:\n  forbidNamespaces:\n    - from: Pricing.Domain.**\n")));
    }
}
