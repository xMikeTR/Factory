using Factory.Core;
using YamlDotNet.Serialization;

namespace Factory.Harness.Policy;

/// off: not measured. report: measured, shown on the pull request, never blocks. block: a ratchet
/// violation fails the task's verify like any other check.
public enum QualityMode { Off, Report, Block }

/// Mutation testing of the lines a task changed. Mode off by default: it needs the tool in the runner image.
public sealed record MutationPolicy(QualityMode Mode, MutationThresholds Thresholds);

/// The UI regression guard: where UI source and UI tests live. Empty lists mean the scan's defaults (ts/tsx/js/jsx; *.test|spec.*).
public sealed record UiPolicy(IReadOnlyList<PathPattern> Globs, IReadOnlyList<PathPattern> TestGlobs);

/// The design allow list's mode (`quality.allowList`), per repo: report or block. A repo it does not name uses `Default`
/// (which the file sets to the global `quality.mode` when `allowList` is absent or names only some repos).
public sealed record AllowListPolicy(QualityMode Default, IReadOnlyDictionary<string, QualityMode> Repos)
{
    /// The mode for a repo; null is the single-repo run, which the file calls ".".
    public QualityMode ModeFor(string? repo) => Repos.TryGetValue(repo ?? ".", out var m) ? m : Default;
}

public sealed record QualityPolicy(QualityMode Mode, QualityThresholds Thresholds, MutationPolicy? Mutation = null, bool Coverage = false,
    IReadOnlyList<ForbiddenDependency>? ForbidRules = null, IReadOnlyList<string>? FlakyCommands = null,
    IReadOnlyList<ForbiddenDependency>? ForbidNamespaceRules = null, UiPolicy? Ui = null, AllowListPolicy? AllowList = null)
{
    /// Namespace dependencies this repo forbids, read from the compiled assemblies (ArchUnitNET). Patterns are namespace paths: Fares/Domain/**.
    public IReadOnlyList<ForbiddenDependency> ForbidNamespaces => ForbidNamespaceRules ?? [];

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
///     forbidNamespaces:               # the same, between namespaces (dotted globs), judged on the compiled assemblies
///       - { from: "Fares.Domain.**", to: "Fares.Infra.**", why: "the domain layer is pure" }
///     allowList: report | block       # the design's allow list (dependencies.json): one word for every repo, or { <repo>: report|block } ("." in a single-repo run); unset inherits `mode`
///     ui: { globs: [...], testGlobs: [...] }  # UI regression guard (on when present; {} = default globs); the contract is .factory/ui-contract.yaml
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
        public List<ForbidEntry>? ForbidNamespaces { get; set; }
        public List<string>? Flaky { get; set; }
        public UiSection? Ui { get; set; }
        public object? AllowList { get; set; }
    }

    private sealed class UiSection { public List<string>? Globs { get; set; } public List<string>? TestGlobs { get; set; } }

    private sealed class ForbidEntry { public string? From { get; set; } public string? To { get; set; } public string? Why { get; set; } }

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static QualityPolicy Load(string repoPath)
    {
        var path = Path.Combine(repoPath, MergePolicyFile.RelativePath);
        var q = PolicyYaml.Read<Doc>(Yaml, path)?.Quality ?? new Section();
        var errors = new List<string>();

        var mode = Mode("mode", q.Mode, Default.Mode, errors);
        var mutation = Mode("mutation", q.Mutation, QualityMode.Off, errors);
        if (q.MaxCyclomatic is <= 0) errors.Add("quality.maxCyclomatic: must be > 0");
        if (q.MaxCrap is <= 0) errors.Add("quality.maxCrap: must be > 0");
        if (q.MaxLines is <= 0) errors.Add("quality.maxLines: must be > 0");
        if (q.MutationMinScore is <= 0 or > 1) errors.Add("quality.mutationMinScore: must be in (0, 1]");

        var forbid = Rules("forbid", q.Forbid, namespaces: false, errors);
        var forbidNamespaces = Rules("forbidNamespaces", q.ForbidNamespaces, namespaces: true, errors);
        var allowList = AllowList(q.AllowList, mode, errors);
        var ui = q.Ui is null ? null : new UiPolicy(Globs("globs", q.Ui.Globs, errors), Globs("testGlobs", q.Ui.TestGlobs, errors));

        if (errors.Count > 0) throw new FactoryRejectedException($"{path} is invalid", errors.ToArray());
        var d = Default.Thresholds;
        return new QualityPolicy(mode!.Value, new QualityThresholds(q.MaxCyclomatic ?? d.MaxCyclomatic, q.MaxCrap ?? d.MaxCrap, q.MaxLines ?? d.MaxLines),
            new MutationPolicy(mutation!.Value, new MutationThresholds(q.MutationMinScore ?? 0.8)), q.Coverage ?? false, forbid, (q.Flaky ?? []).Where(f => !string.IsNullOrWhiteSpace(f)).ToList(), forbidNamespaces, ui, allowList);
    }

    /// `allowList`: absent inherits the global mode; a word applies to every repo; a map names repos (the others inherit). `off` is not offered.
    private static AllowListPolicy? AllowList(object? raw, QualityMode? global, List<string> errors)
    {
        if (raw is null) return null;
        var inherit = global ?? QualityMode.Report;
        QualityMode? Word(string key, object? v)
        {
            var s = v?.ToString();
            QualityMode? m = s?.ToLowerInvariant() switch { "report" => QualityMode.Report, "block" => QualityMode.Block, _ => null };
            if (m is null) errors.Add($"quality.allowList{key}: '{s}' is not report|block");
            return m;
        }
        if (raw is not IDictionary<object, object> map) return Word("", raw) is { } all ? new AllowListPolicy(all, new Dictionary<string, QualityMode>()) : null;
        var repos = new Dictionary<string, QualityMode>(StringComparer.Ordinal);
        foreach (var (k, v) in map)
        {
            var name = k?.ToString()?.Trim();
            if (string.IsNullOrEmpty(name)) { errors.Add("quality.allowList: a repo name is blank"); continue; }
            if (Word("." + name, v) is { } m) repos[name] = m;
        }
        return new AllowListPolicy(inherit, repos);
    }

    /// Repo names `quality.allowList` names that this run does not have: like `CodegenPolicyFile.Unknown`, "." is the single-repo run.
    public static IReadOnlyList<string> UnknownAllowListRepos(QualityPolicy? policy, IReadOnlyCollection<string> runRepos) =>
        policy?.AllowList is not { } list ? []
        : runRepos.Count > 0
            ? list.Repos.Keys.Where(n => !runRepos.Contains(n)).Select(n => $"quality.allowList names '{n}', which is not a repo of this run ({string.Join(", ", runRepos.Order(StringComparer.Ordinal))})").ToList()
            : list.Repos.Keys.Where(n => n != ".").Select(n => $"quality.allowList names '{n}', but this is a single-repo run (use \".\")").ToList();

    /// null (and an error) for a word that is not off|report|block; an absent value is the fallback.
    private static QualityMode? Mode(string key, string? value, QualityMode fallback, List<string> errors)
    {
        QualityMode? mode = value?.ToLowerInvariant() switch
        {
            null => fallback,
            "off" => QualityMode.Off,
            "report" => QualityMode.Report,
            "block" => QualityMode.Block,
            _ => null,
        };
        if (mode is null) errors.Add($"quality.{key}: '{value}' is not off|report|block");
        return mode;
    }

    private static List<ForbiddenDependency> Rules(string section, IEnumerable<ForbidEntry>? entries, bool namespaces, List<string> errors)
    {
        var rules = new List<ForbiddenDependency>();
        foreach (var (e, i) in (entries ?? []).Select((e, i) => (e, i)))
        {
            if (string.IsNullOrWhiteSpace(e.From) || string.IsNullOrWhiteSpace(e.To) || string.IsNullOrWhiteSpace(e.Why))
            { errors.Add($"quality.{section}[{i}]: from, to and why are all required"); continue; }
            var from = PathPatternModule.create(namespaces ? e.From.Replace('.', '/') : e.From);
            var to = PathPatternModule.create(namespaces ? e.To.Replace('.', '/') : e.To);
            if (from.IsError) errors.Add($"quality.{section}[{i}].from: {from.ErrorValue}");
            else if (to.IsError) errors.Add($"quality.{section}[{i}].to: {to.ErrorValue}");
            else rules.Add(new ForbiddenDependency(from.ResultValue, to.ResultValue, e.Why));
        }
        return rules;
    }

    private static List<PathPattern> Globs(string name, IEnumerable<string>? globs, List<string> errors)
    {
        var patterns = new List<PathPattern>();
        foreach (var g in globs ?? [])
        {
            var p = PathPatternModule.create(g ?? "");
            if (p.IsError) errors.Add($"quality.ui.{name}: {p.ErrorValue}"); else patterns.Add(p.ResultValue);
        }
        return patterns;
    }
}
