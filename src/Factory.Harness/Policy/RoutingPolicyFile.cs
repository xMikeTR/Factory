using Factory.Core;
using Factory.Harness.Routing;
using Microsoft.FSharp.Core;
using YamlDotNet.Serialization;

namespace Factory.Harness.Policy;

/// Reads the `routing:` section of the target repo's .factory/policy.yaml (the protected file, read from the base
/// checkout). Every key is optional; a missing section is `Router.defaultPolicy` plus the defaults below.
///
///   routing:
///     minTier: { low: small, medium: standard, high: standard }   # lowest tier a risk may route to
///     dataClass: internal                                          # public | internal | regulated, for every task
///     estimates:                                                   # tokens per attempt until stats exist
///       implementer: { in: 40000, out: 8000 }                      # planner, taskPlanner, testAuthor, implementer, reviewer, qa
///     sizeBuckets: { s: 4, m: 10 }                                 # max writes + testWrites for size S, and for M
///     canaryShare: 0.05                                            # share of (task, role) pairs whose Standard entry is the Frontier model
///     halfLifeDays: 30                                             # evidence from the ledger halves in weight every this many days
///     learnReviewer: false                                         # count reviewer verdicts as evidence (weak)
///
/// The estimate and size-bucket defaults are placeholders until phase 3 has real data. Invalid values are rejected
/// together, naming the file.
public static class RoutingPolicyFile
{
    public sealed record RoutingConfig(RoutingPolicy Policy, TaskSpecConfig Spec, double CanaryShare = 0.05, LearningSettings? LearningOverride = null)
    {
        public LearningSettings Learning => LearningOverride ?? new LearningSettings();
    }

    private sealed class Doc { public Section? Routing { get; set; } }

    private sealed class Section
    {
        public Dictionary<string, string>? MinTier { get; set; }
        public string? DataClass { get; set; }
        public Dictionary<string, Est>? Estimates { get; set; }
        public Buckets? SizeBuckets { get; set; }
        public double? CanaryShare { get; set; }
        public double? HalfLifeDays { get; set; }
        public bool? LearnReviewer { get; set; }
    }

    private sealed class Est { public int In { get; set; } public int Out { get; set; } }
    private sealed class Buckets { public int? S { get; set; } public int? M { get; set; } }

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly Dictionary<string, Role> Roles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["planner"] = Role.Planner, ["taskPlanner"] = Role.TaskPlanner, ["testAuthor"] = Role.TestAuthor,
        ["implementer"] = Role.Implementer, ["reviewer"] = Role.Reviewer, ["qa"] = Role.Qa,
    };

    private static readonly Dictionary<string, (int In, int Out)> DefaultEstimates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["planner"] = (30_000, 6_000), ["taskPlanner"] = (20_000, 4_000), ["testAuthor"] = (20_000, 4_000),
        ["implementer"] = (40_000, 8_000), ["reviewer"] = (30_000, 2_000), ["qa"] = (20_000, 2_000),
    };

    public static RoutingConfig Load(string repoPath)
    {
        var path = Path.Combine(repoPath, MergePolicyFile.RelativePath);
        var s = PolicyYaml.Read<Doc>(Yaml, path)?.Routing ?? new Section();
        var errors = new List<string>();

        ModelTier? Tier(string risk, ModelTier fallback)
        {
            if (s.MinTier is null || !s.MinTier.TryGetValue(risk, out var v)) return fallback;
            switch (v.ToLowerInvariant())
            {
                case "small": return ModelTier.Small;
                case "standard": return ModelTier.Standard;
                case "frontier": return ModelTier.Frontier;
                default: errors.Add($"routing.minTier.{risk}: '{v}' is not small|standard|frontier"); return null;
            }
        }
        foreach (var k in (s.MinTier ?? []).Keys)
            if (!new[] { "low", "medium", "high" }.Contains(k, StringComparer.OrdinalIgnoreCase)) errors.Add($"routing.minTier.{k}: not low|medium|high");
        var low = Tier("low", ModelTier.Small) ?? ModelTier.Small;
        var medium = Tier("medium", ModelTier.Standard) ?? ModelTier.Standard;
        var high = Tier("high", ModelTier.Standard) ?? ModelTier.Standard;

        var dataClass = DataClass.Internal;
        if (s.DataClass is { } dc)
            switch (dc.ToLowerInvariant())
            {
                case "public": dataClass = DataClass.Public; break;
                case "internal": dataClass = DataClass.Internal; break;
                case "regulated": dataClass = DataClass.Regulated; break;
                default: errors.Add($"routing.dataClass: '{dc}' is not public|internal|regulated"); break;
            }

        var est = DefaultEstimates.ToDictionary(kv => Roles[kv.Key], kv => (kv.Value.In, kv.Value.Out));
        foreach (var (name, e) in s.Estimates ?? [])
        {
            if (!Roles.TryGetValue(name, out var role)) { errors.Add($"routing.estimates.{name}: not a role ({string.Join(", ", Roles.Keys)})"); continue; }
            if (e.In < 0 || e.Out < 0) { errors.Add($"routing.estimates.{name}: in and out must be >= 0"); continue; }
            est[role] = (e.In, e.Out);
        }

        var small = s.SizeBuckets?.S ?? 4;
        var med = s.SizeBuckets?.M ?? 10;
        if (small < 0 || med < small) errors.Add("routing.sizeBuckets: need 0 <= s <= m");

        var canary = s.CanaryShare ?? 0.05;
        if (canary is < 0 or > 1) errors.Add("routing.canaryShare: need 0 <= share <= 1");
        var halfLife = s.HalfLifeDays ?? 30;
        if (halfLife <= 0) errors.Add("routing.halfLifeDays: must be > 0");

        if (errors.Count > 0) throw new FactoryRejectedException($"{path} is invalid", errors.ToArray());

        var policy = new RoutingPolicy(
            FuncConvert.FromFunc<RiskLevel, ModelTier>(r => r.IsLow ? low : r.IsMedium ? medium : high),
            Router.defaultPolicy.MinSamplesForMeans);
        var spec = new TaskSpecConfig(small, med, dataClass,
            FuncConvert.FromFunc<Role, Tuple<int, int>>(r => { var (i, o) = est[r]; return Tuple.Create(i, o); }));
        return new RoutingConfig(policy, spec, canary, new LearningSettings(halfLife, s.LearnReviewer ?? false));
    }
}
