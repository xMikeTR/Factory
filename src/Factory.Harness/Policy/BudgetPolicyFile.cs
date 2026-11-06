using Factory.Core;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using YamlDotNet.Serialization;

namespace Factory.Harness.Policy;

/// Reads the `budget:` section of the target repo's .factory/policy.yaml (the same protected file as the merge,
/// quality and npm policy, read from the base checkout):
///
///   budget:
///     itemTokens: 2000000        # default limit per work item; absent = no limit
///     items:
///       W-BIG: 5000000           # per-item override
///
/// A missing section means no limit anywhere, so nothing is reported. A negative limit is rejected.
public static class BudgetPolicyFile
{
    private sealed class Doc { public Section? Budget { get; set; } }

    private sealed class Section
    {
        public long? ItemTokens { get; set; }
        public Dictionary<string, long>? Items { get; set; }
    }

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static BudgetPolicy Load(string repoPath)
    {
        var path = Path.Combine(repoPath, MergePolicyFile.RelativePath);
        var b = PolicyYaml.Read<Doc>(Yaml, path)?.Budget ?? new Section();
        var errors = new List<string>();
        if (b.ItemTokens is < 0) errors.Add("budget.itemTokens: must be >= 0");
        errors.AddRange((b.Items ?? []).Where(kv => kv.Value < 0).Select(kv => $"budget.items.{kv.Key}: must be >= 0"));
        if (errors.Count > 0) throw new FactoryRejectedException($"{path} is invalid", errors.ToArray());
        return new BudgetPolicy(
            b.ItemTokens is { } d ? FSharpOption<long>.Some(d) : FSharpOption<long>.None,
            MapModule.OfSeq((b.Items ?? []).Select(kv => Tuple.Create(kv.Key, kv.Value))));
    }
}

/// The cost side of `budget:` (phase 4), alongside the token limits above. All optional; a missing field means no such limit.
///
///   budget:
///     itemCost: 50               # per work item, in `currency`, from tokens x imported price
///     userMonthlyCost: 750       # the user's monthly spend cap, from the latest spend import
///     softAt: 0.8                # fraction of a limit where the state turns Soft
///     downgradeAt: 0.9           # ... and Downgrade (tier capped at Standard)
///     maxImportAgeDays: 2        # a spend import older than this is unknown: Soft, with a warning
///     currency: GBP
public sealed record CostBudgetPolicy(decimal? ItemCost, decimal? UserMonthlyCost, BudgetLimits Limits, int MaxImportAgeDays, string Currency)
{
    public static readonly CostBudgetPolicy None = new(null, null, new BudgetLimits(0.8, 0.9), 2, "GBP");
}

public static class CostBudgetPolicyFile
{
    private sealed class Doc { public Section? Budget { get; set; } }

    private sealed class Section
    {
        public decimal? ItemCost { get; set; }
        public decimal? UserMonthlyCost { get; set; }
        public double? SoftAt { get; set; }
        public double? DowngradeAt { get; set; }
        public int? MaxImportAgeDays { get; set; }
        public string? Currency { get; set; }
    }

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static CostBudgetPolicy Load(string repoPath)
    {
        var path = Path.Combine(repoPath, MergePolicyFile.RelativePath);
        var b = PolicyYaml.Read<Doc>(Yaml, path)?.Budget ?? new Section();
        var soft = b.SoftAt ?? CostBudgetPolicy.None.Limits.SoftAt;
        var down = b.DowngradeAt ?? CostBudgetPolicy.None.Limits.DowngradeAt;
        var errors = new List<string>();
        if (b.ItemCost is < 0) errors.Add("budget.itemCost: must be >= 0");
        if (b.UserMonthlyCost is < 0) errors.Add("budget.userMonthlyCost: must be >= 0");
        if (soft is <= 0 or > 1) errors.Add("budget.softAt: must be in (0, 1]");
        if (down is <= 0 or > 1) errors.Add("budget.downgradeAt: must be in (0, 1]");
        if (soft > down) errors.Add("budget.softAt: must not exceed downgradeAt");
        if (b.MaxImportAgeDays is < 0) errors.Add("budget.maxImportAgeDays: must be >= 0");
        if (errors.Count > 0) throw new FactoryRejectedException($"{path} is invalid", errors.ToArray());
        return new CostBudgetPolicy(b.ItemCost, b.UserMonthlyCost, new BudgetLimits(soft, down),
            b.MaxImportAgeDays ?? CostBudgetPolicy.None.MaxImportAgeDays, b.Currency ?? CostBudgetPolicy.None.Currency);
    }
}
