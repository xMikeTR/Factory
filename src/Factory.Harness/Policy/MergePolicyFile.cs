using Factory.Core;
using Microsoft.FSharp.Collections;
using YamlDotNet.Serialization;

namespace Factory.Harness.Policy;

using Core = Factory.Core.Core;

/// Reads the target repo's .factory/policy.yaml into the F# MergePolicy. The file lives on the
/// base checkout, never a task worktree, so no agent edits the policy that judges its own merge.
///
///   merge:
///     enforcement: auto | tiered      # FACTORY_MERGE_ENFORCEMENT overrides
///     approvers: [alice, bob]         # FACTORY_MERGE_APPROVERS (comma-separated) overrides
///     autoMergeMaxRisk: low | medium | high
///     autoMergeMaxChangedLines: 400
///
/// A missing file is the default (auto: merge on green). An unknown value is rejected.
public static class MergePolicyFile
{
    public const string RelativePath = ".factory/policy.yaml";

    private sealed class Doc { public Section? Merge { get; set; } public object? Quality { get; set; } /* read by QualityPolicyFile */ public object? Npm { get; set; } /* read by NpmPolicyFile */ public object? Budget { get; set; } /* read by BudgetPolicyFile */ }

    private sealed class Section
    {
        public string? Enforcement { get; set; }
        public List<string>? Approvers { get; set; }
        public string? AutoMergeMaxRisk { get; set; }
        public int? AutoMergeMaxChangedLines { get; set; }
    }

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
        .Build();

    public static MergePolicy FromEnvironment(string repoPath) =>
        Load(repoPath, Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? ""));

    public static MergePolicy Load(string repoPath, IReadOnlyDictionary<string, string> env)
    {
        var path = Path.Combine(repoPath, RelativePath);
        var m = (File.Exists(path) ? Yaml.Deserialize<Doc?>(File.ReadAllText(path)) : null)?.Merge ?? new Section();
        var d = Core.DefaultMergePolicy;
        var errors = new List<string>();

        var mode = env.GetValueOrDefault("FACTORY_MERGE_ENFORCEMENT") ?? m.Enforcement ?? "auto";
        var approvers = env.TryGetValue("FACTORY_MERGE_APPROVERS", out var a)
            ? a.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : m.Approvers ?? [];
        MergeEnforcement? enforcement = mode.ToLowerInvariant() switch
        {
            "auto" => MergeEnforcement.Auto,
            "tiered" => MergeEnforcement.NewTiered(ListModule.OfSeq(approvers)),
            _ => null,
        };
        if (enforcement is null) errors.Add($"merge.enforcement: '{mode}' is not auto|tiered");

        RiskLevel? risk = (m.AutoMergeMaxRisk?.ToLowerInvariant()) switch
        {
            null => d.AutoMergeMaxRisk,
            "low" => RiskLevel.Low,
            "medium" => RiskLevel.Medium,
            "high" => RiskLevel.High,
            _ => null,
        };
        if (risk is null) errors.Add($"merge.autoMergeMaxRisk: '{m.AutoMergeMaxRisk}' is not low|medium|high");

        var lines = m.AutoMergeMaxChangedLines ?? d.AutoMergeMaxChangedLines;
        if (lines < 0) errors.Add("merge.autoMergeMaxChangedLines: must be >= 0");

        if (errors.Count > 0) throw new FactoryRejectedException($"{path} is invalid", errors.ToArray());
        return new MergePolicy(enforcement!, risk!, lines);
    }
}
