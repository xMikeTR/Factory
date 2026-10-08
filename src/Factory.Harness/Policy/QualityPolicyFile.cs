using Factory.Core;
using YamlDotNet.Serialization;

namespace Factory.Harness.Policy;

/// off: not measured. report: measured, shown on the pull request, never blocks. block: a ratchet
/// violation fails the task's verify like any other check.
public enum QualityMode { Off, Report, Block }

public sealed record QualityPolicy(QualityMode Mode, QualityThresholds Thresholds);

/// The `quality:` section of the target repo's .factory/policy.yaml (same file and same trust rule as
/// MergePolicyFile: read from the base checkout, never a task worktree).
///
///   quality:
///     mode: off | report | block      # default report
///     maxCyclomatic: 15
///     maxCrap: 30
///     maxLines: 80
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

        if (errors.Count > 0) throw new FactoryRejectedException($"{path} is invalid", errors.ToArray());
        return new QualityPolicy(mode!.Value, new QualityThresholds(q.MaxCyclomatic ?? d.MaxCyclomatic, q.MaxCrap ?? d.MaxCrap, q.MaxLines ?? d.MaxLines));
    }
}
