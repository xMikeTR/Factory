using Factory.Core;
using YamlDotNet.Serialization;

namespace Factory.Harness.Policy;

/// off: not measured. report: measured, shown on the pull request, never blocks. block: a ratchet
/// violation fails the task's verify like any other check.
public enum QualityMode { Off, Report, Block }

/// Mutation testing of the lines a task changed. Mode off by default: it needs the tool in the runner image.
public sealed record MutationPolicy(QualityMode Mode, MutationThresholds Thresholds);

public sealed record QualityPolicy(QualityMode Mode, QualityThresholds Thresholds, MutationPolicy? Mutation = null, bool Coverage = false,
    IReadOnlyList<ForbiddenDependency>? ForbidRules = null, IReadOnlyList<string>? FlakyCommands = null)
{
    /// Commands (substrings) whose plain failure is re-run once alone: the repo says they are flaky.
    public IReadOnlyList<string> Flaky => FlakyCommands ?? [];

    /// Hand-written architecture rules: project references from `From` to `To` that this repo forbids.
    public IReadOnlyList<ForbiddenDependency> Forbid => ForbidRules ?? [];
}

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
///     forbid:                         # hand-written fitness rules: project references the repo forbids
///       - { from: "src/Domain/**", to: "src/Infra/**", why: "the domain layer is pure" }
///     flaky: ["dotnet test --filter Slow"]  # commands (substrings) whose failure is re-run once alone
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
        public List<ForbidEntry>? Forbid { get; set; }
        public List<string>? Flaky { get; set; }
    }

    private sealed class ForbidEntry { public string? From { get; set; } public string? To { get; set; } public string? Why { get; set; } }

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

        var forbid = new List<ForbiddenDependency>();
        foreach (var (e, i) in (q.Forbid ?? []).Select((e, i) => (e, i)))
        {
            if (string.IsNullOrWhiteSpace(e.From) || string.IsNullOrWhiteSpace(e.To) || string.IsNullOrWhiteSpace(e.Why))
            { errors.Add($"quality.forbid[{i}]: from, to and why are all required"); continue; }
            var from = PathPatternModule.create(e.From);
            var to = PathPatternModule.create(e.To);
            if (from.IsError) errors.Add($"quality.forbid[{i}].from: {from.ErrorValue}");
            else if (to.IsError) errors.Add($"quality.forbid[{i}].to: {to.ErrorValue}");
            else forbid.Add(new ForbiddenDependency(from.ResultValue, to.ResultValue, e.Why));
        }

        if (errors.Count > 0) throw new FactoryRejectedException($"{path} is invalid", errors.ToArray());
        return new QualityPolicy(mode!.Value, new QualityThresholds(q.MaxCyclomatic ?? d.MaxCyclomatic, q.MaxCrap ?? d.MaxCrap, q.MaxLines ?? d.MaxLines),
            new MutationPolicy(mutation!.Value, new MutationThresholds(q.MutationMinScore ?? 0.8)), q.Coverage ?? false, forbid, (q.Flaky ?? []).Where(f => !string.IsNullOrWhiteSpace(f)).ToList());
    }
}
