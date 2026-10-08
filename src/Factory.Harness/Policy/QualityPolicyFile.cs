using Factory.Core;
using YamlDotNet.Serialization;

namespace Factory.Harness.Policy;

/// off: not measured. report: measured, shown on the pull request, never blocks. block: a ratchet
/// violation fails the task's verify like any other check.
public enum QualityMode { Off, Report, Block }

/// Mutation testing of the lines a task changed. Mode off by default: it needs the tool in the runner image.
public sealed record MutationPolicy(QualityMode Mode, MutationThresholds Thresholds);

public sealed record QualityPolicy(QualityMode Mode, QualityThresholds Thresholds, MutationPolicy? Mutation = null, bool Coverage = false);

/// The `quality:` section of the target repo's .factory/policy.yaml (same file and same trust rule as
/// MergePolicyFile: read from the base checkout, never a task worktree).
///
///   quality:
///     mode: off | report | block      # default report
///     maxCyclomatic: 15
///     maxCrap: 30
///     maxLines: 80
///     mutation: off | report | block  # default off
///     coverage: false                 # true: run the tests under coverlet so CRAP is judged (one more test run per verify)
///     mutationMinScore: 0.8           # share of mutants on changed lines the tests must kill
public static class QualityPolicyFile
{
    public static readonly QualityPolicy Default = new(QualityMode.Report, new QualityThresholds(15, 30.0, 80));

    private sealed class Doc { public Section? Quality { get; set; } }

    private sealed class Section
    {
        public string? Mode { get; set; }
        public int? MaxCyclomatic { get; set; }
        public double? MaxCrap { get; set; }
        public int? MaxLines { get; set; }
        public string? Mutation { get; set; }
        public double? MutationMinScore { get; set; }
        public bool? Coverage { get; set; }
    }

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static QualityPolicy Load(string repoPath)
    {
        var path = Path.Combine(repoPath, MergePolicyFile.RelativePath);
        var q = (File.Exists(path) ? Yaml.Deserialize<Doc?>(File.ReadAllText(path)) : null)?.Quality ?? new Section();
        var d = Default.Thresholds;
        var errors = new List<string>();

        QualityMode? mode = q.Mode?.ToLowerInvariant() switch
        {
            null => Default.Mode,
            "off" => QualityMode.Off,
            "report" => QualityMode.Report,
            "block" => QualityMode.Block,
            _ => null,
        };
        if (mode is null) errors.Add($"quality.mode: '{q.Mode}' is not off|report|block");
        if (q.MaxCyclomatic is <= 0) errors.Add("quality.maxCyclomatic: must be > 0");
        if (q.MaxCrap is <= 0) errors.Add("quality.maxCrap: must be > 0");
        if (q.MaxLines is <= 0) errors.Add("quality.maxLines: must be > 0");

        QualityMode? mutation = q.Mutation?.ToLowerInvariant() switch
        {
            null or "off" => QualityMode.Off,
            "report" => QualityMode.Report,
            "block" => QualityMode.Block,
            _ => null,
        };
        if (mutation is null) errors.Add($"quality.mutation: '{q.Mutation}' is not off|report|block");
        if (q.MutationMinScore is <= 0 or > 1) errors.Add("quality.mutationMinScore: must be in (0, 1]");

        if (errors.Count > 0) throw new FactoryRejectedException($"{path} is invalid", errors.ToArray());
        return new QualityPolicy(mode!.Value, new QualityThresholds(q.MaxCyclomatic ?? d.MaxCyclomatic, q.MaxCrap ?? d.MaxCrap, q.MaxLines ?? d.MaxLines),
            new MutationPolicy(mutation!.Value, new MutationThresholds(q.MutationMinScore ?? 0.8)), q.Coverage ?? false);
    }
}
