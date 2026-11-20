using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Harness.Policy;
using Factory.Harness.Registry;
using Factory.S2.Git;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;

namespace Factory.S2;

/// The deterministic codegen phase (R5a). Before the planner: the pinned OpenAPI texts become C# DTOs, server interfaces and clients
/// in `{runDir}/codegen/out/<repo>/` (X3: and TypeScript types plus a thin client). After the integration branch exists they are seeded into `generated/contracts/`, where
/// they are read-only to agents (protected path) and drift-checked in integration verify.
public static class CodegenPhase
{
    public const string ManifestFile = "manifest.json";
    private const string BaselineFile = "codegen-baseline.json";

    public static string WorkDir(string runDir) => Path.Combine(runDir, "codegen");

    /// Null when the run goes on; a `codegen_failed` finish when generation was needed and did not happen.
    public static async Task<RunFinished?> GenerateAsync(FactoryServices svc, string runId, Handover handover, CancellationToken ct)
    {
        const string Stage = "codegen";
        var work = WorkDir(svc.Settings.RunDir(runId));
        var manifestPath = Path.Combine(work, ManifestFile);

        if (!svc.Settings.CodegenOn)
        {
            await svc.LogAsync(runId, Stage, LedgerKinds.Codegen, outcome: "off", ct: ct);
            if (handover.ApiScenarios.Any(Codegen.hasGeneratedExamples))
                await svc.LogAsync(runId, Stage, LedgerKinds.ApiScenariosSkipped, outcome: "skipped",
                    payload: JsonSerializer.Serialize(new { reason = "codegen off", scenarios = handover.ApiScenarios.Where(Codegen.hasGeneratedExamples).Select(a => a.Id).ToList() }), ct: ct);
            return null;
        }
        if (File.Exists(manifestPath)) // resume: what was generated stays
        {
            await svc.LogAsync(runId, Stage, LedgerKinds.Codegen, outcome: "reused", payload: await File.ReadAllTextAsync(manifestPath, ct), ct: ct);
            return null;
        }

        var names = svc.Settings.CodegenCsharp;
        var tsNames = svc.Settings.CodegenTypescript;
        var bad = new List<string>();
        if (names is not null) bad.AddRange(CodegenPolicyFile.Unknown(names, svc.Settings.RepoNames));
        if (tsNames is not null) bad.AddRange(CodegenPolicyFile.Unknown(tsNames, svc.Settings.RepoNames, "codegen.typescript"));
        if (bad.Count > 0) throw new FactoryRejectedException(".factory/policy.yaml codegen is invalid", [.. bad]);
        FSharpOption<FSharpList<string>> Opt(IReadOnlyList<string>? l) => l is null ? FSharpOption<FSharpList<string>>.None : FSharpOption<FSharpList<string>>.Some(ListModule.OfSeq(l));
        var targets = Codegen.targets(handover, Opt(names));
        if (targets.IsError) throw new FactoryRejectedException("contracts cannot be generated", [.. targets.ErrorValue]);
        var nodeRepos = new List<string>();
        foreach (var repo in handover.Elements.Select(e => e.Repo is { } r ? r.Value : ".").Distinct().Order(StringComparer.Ordinal))
            // The base branch decides, not the working tree: an uncommitted package.json must not change what is generated.
            if (await GitFor(svc, repo).BaseHasFileAsync(svc.Settings.BaseBranch, "package.json", ct)) nodeRepos.Add(repo);
        var tsTargets = Codegen.tsTargets(handover, Opt(tsNames), SetModule.OfSeq(nodeRepos));
        if (tsTargets.IsError) throw new FactoryRejectedException("contracts cannot be generated", [.. tsTargets.ErrorValue]);
        var list = targets.ResultValue.Concat(tsTargets.ResultValue).ToList();
        if (list.Count == 0)
        {
            await svc.LogAsync(runId, Stage, LedgerKinds.Codegen, outcome: "none", ct: ct);
            return null;
        }

        // Fresh inputs and outputs every time: nothing a half-finished earlier attempt left can leak into the result.
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        var errors = new List<string>();
        foreach (var t in handover.ApiContractTexts)
        {
            if (DesigncHandoverReader.Sha256(t.Text) != t.Sha256) { errors.Add($"{t.Path} does not match its sha256 (changed after the handover was frozen)"); continue; }
            var full = Path.Combine(work, "in", t.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, t.Text, ct);
        }
        foreach (var t in list.Where(t => !File.Exists(Path.Combine(work, "in", t.Source))))
            errors.Add($"contracts_api '{t.Source}' has no frozen text in the handover");
        if (errors.Count > 0) throw new FactoryRejectedException("contracts cannot be generated", [.. errors]);

        var run = await svc.CodeGenerator.GenerateAsync(list, work, ct);
        if (run.Outcome == "generated")
        {
            // The Factory's own thin client: a pure function of the pinned operations, written next to the tool's schema.
            foreach (var t in list.Where(t => t.Language.IsTypeScript && !t.Operations.IsEmpty))
            {
                var path = Path.Combine(work, Codegen.outPath(t.Repo, t.Files.First(f => f.EndsWith(".client.ts", StringComparison.Ordinal))));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, Codegen.tsClient(t, handover.ApiContractTexts.First(x => x.Path == t.Source).Sha256), ct);
            }
        }
        var apiFiles = new List<(string Repo, string Path, string Text)>();
        if (run.Outcome == "generated")
        {
            // X4: the API behaviour tests are the Factory's own text, a pure function of the handover; no tool runs for them.
            var api = ApiScenarios.files(handover);
            if (api.IsError)
            {
                var detail = string.Join("; ", api.ErrorValue);
                await svc.LogAsync(runId, Stage, LedgerKinds.Codegen, outcome: "failed", payload: JsonSerializer.Serialize(new { detail, commands = run.Commands }), ct: ct);
                return new RunFinished(runId, "codegen_failed", detail);
            }
            var generatedRepos = list.Select(t => t.Repo).ToHashSet(StringComparer.Ordinal);
            foreach (var (repo, rel, text) in api.ResultValue.Select(f => (f.Item1, f.Item2, f.Item3)).Where(f => generatedRepos.Contains(f.Item1)))
            {
                var path = Path.Combine(work, Codegen.outPath(repo, rel));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, text, ct);
                apiFiles.Add((repo, rel, text));
            }
        }
        if (run.Outcome == "generated")
            foreach (var f in list.SelectMany(t => t.Files.Select(f => (t, f))).Where(x => !File.Exists(Path.Combine(work, Codegen.outPath(x.t.Repo, x.f)))))
                run = new CodegenRun("failed", $"the generator reported success but did not write {Codegen.outPath(f.t.Repo, f.f)}", run.Commands);
        if (run.Outcome != "generated")
        {
            await svc.LogAsync(runId, Stage, LedgerKinds.Codegen, outcome: run.Outcome,
                payload: JsonSerializer.Serialize(new { detail = run.Detail, commands = run.Commands }), ct: ct);
            return new RunFinished(runId, "codegen_failed", run.Detail);
        }

        var manifest = Manifest(list, work, handover, apiFiles);
        await File.WriteAllTextAsync(manifestPath, manifest, ct);
        await svc.LogAsync(runId, Stage, LedgerKinds.Codegen, outcome: "generated", payload: manifest, ct: ct);
        return null;
    }

    /// Manifest v2: `{tools:[{name,version}], files:[{repo,path,sha256,source,source_sha256}], sources:[{path,sha256,operations,schemas}]}`,
    /// sorted: the same inputs give the same bytes.
    private static string Manifest(List<Codegen.Target> targets, string work, Handover handover, List<(string Repo, string Path, string Text)> apiFiles)
    {
        var texts = handover.ApiContractTexts.ToDictionary(t => t.Path, t => t, StringComparer.Ordinal);
        var tools = new[] { (name: "nswag", version: Codegen.nswagVersion, on: targets.Any(t => t.Language.IsCSharp)),
                            (name: "openapi-typescript", version: Codegen.openapiTypescriptVersion, on: targets.Any(t => t.Language.IsTypeScript)) }
            .Where(t => t.on).Select(t => new { t.name, t.version }).OrderBy(t => t.name, StringComparer.Ordinal).ToList();
        var files = targets
            .SelectMany(t => t.Files.Select(f => new
            {
                repo = t.Repo,
                path = f,
                sha256 = UiPreview.Sha256Hex(File.ReadAllBytes(Path.Combine(work, Codegen.outPath(t.Repo, f)))),
                source = t.Source,
                source_sha256 = texts[t.Source].Sha256,
            }))
            .Select(f => (repo: f.repo, path: f.path, row: (object)f))
            .Concat(apiFiles.Select(f =>
            {
                var scenario = handover.ApiScenarios.FirstOrDefault(a => f.Path.EndsWith("/" + ApiScenarios.idPascal(a.Id) + ".ApiScenario.g.cs", StringComparison.Ordinal));
                var row = new Dictionary<string, object?> { ["repo"] = f.Repo, ["path"] = f.Path, ["sha256"] = UiPreview.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(f.Text)) };
                if (scenario is not null) { row["source"] = scenario.Path; row["source_sha256"] = scenario.Sha256; }
                row["tool"] = "factory";
                return (repo: f.Repo, path: f.Path, row: (object)row);
            }))
            .OrderBy(f => f.repo, StringComparer.Ordinal).ThenBy(f => f.path, StringComparer.Ordinal).Select(f => f.row).ToList();
        var sources = targets.Select(t => t.Source).Distinct().Order(StringComparer.Ordinal)
            .Select(p =>
            {
                var index = OpenApiIndex.Read(texts[p].Text);
                return new
                {
                    path = p,
                    sha256 = texts[p].Sha256,
                    operations = index.Operations.Select(o => new { id = o.Id, method = o.Method, path = o.Path }).ToList(),
                    schemas = index.Schemas,
                };
            }).ToList();
        return JsonSerializer.Serialize(new { tools, files, sources });
    }

    private sealed record ManifestFileRow(string repo, string path, string? source = null);
    private sealed record ManifestOp(string? id, string method, string path);
    private sealed record ManifestSource(string path, List<ManifestOp>? operations = null, List<string>? schemas = null);
    private sealed record ManifestDoc(List<ManifestFileRow> files, List<ManifestSource>? sources = null);

    /// What the hand-written boundary check compares added code with, for one repo: the operations of the contracts the repo has generated code for,
    /// and the schema names of those whose generated code here has no server side (a server's own domain types are legitimate). Empty when the
    /// manifest lists no files for the repo.
    public static (IReadOnlyList<GeneratedBoundary.Op> Ops, IReadOnlyList<(string Source, string Name)> Dtos) BoundaryInputs(string runDir, string repo)
    {
        var path = Path.Combine(WorkDir(runDir), ManifestFile);
        if (!File.Exists(path)) return ([], []);
        var doc = JsonSerializer.Deserialize<ManifestDoc>(File.ReadAllText(path));
        var mine = doc?.files.Where(f => f.repo == repo && f.source is not null).ToList() ?? [];
        var ops = new List<GeneratedBoundary.Op>();
        var dtos = new List<(string, string)>();
        foreach (var src in doc?.sources ?? [])
        {
            var files = mine.Where(f => f.source == src.path).ToList();
            if (files.Count == 0) continue;
            ops.AddRange((src.operations ?? []).Select(o => new GeneratedBoundary.Op(src.path, o.id is null ? FSharpOption<string>.None : FSharpOption<string>.Some(o.id), o.method, o.path)));
            if (!files.Any(f => f.path.EndsWith(".Server.g.cs", StringComparison.Ordinal)))
                dtos.AddRange((src.schemas ?? []).Select(n => (src.path, n)));
        }
        return (ops, dtos);
    }

    /// What the manifest lists as generated: (repo, path, source). Empty when nothing was generated.
    public static IReadOnlyList<(string Repo, string Path, string Source)> GeneratedFiles(string runDir)
    {
        var path = Path.Combine(WorkDir(runDir), ManifestFile);
        if (!File.Exists(path)) return [];
        var doc = JsonSerializer.Deserialize<ManifestDoc>(File.ReadAllText(path));
        return doc?.files.Select(f => (f.repo, f.path, f.source ?? "")).ToList() ?? [];
    }

    /// The brief section naming the generated code for these elements (null: all), or null when there is none.
    public static string? BriefSection(Handover handover, IReadOnlyList<(string Repo, string Path, string Source)> files, IEnumerable<string>? elements = null)
    {
        if (files.Count == 0) return null;
        var lines = Codegen.briefLines(handover, ListModule.OfSeq(files.Select(f => Tuple.Create(f.Repo, f.Path, f.Source))),
            elements is null ? FSharpOption<FSharpList<string>>.None : FSharpOption<FSharpList<string>>.Some(ListModule.OfSeq(elements)));
        var section = Codegen.briefSection(lines);
        return FSharpOption<string>.get_IsSome(section) ? section.Value : null;
    }

    private static GitWorkspace GitFor(FactoryServices svc, string repo) =>
        repo != "." && svc.RepoGits.TryGetValue(repo, out var g) ? g : svc.Git;

    /// Once per run, after the integration branches exist: replace `generated/contracts/` with the generated files and commit
    /// them as the Factory's own. A resume finds the baseline and does nothing.
    public static async Task SeedAsync(FactoryServices svc, string runId, CancellationToken ct)
    {
        var runDir = svc.Settings.RunDir(runId);
        var manifestPath = Path.Combine(WorkDir(runDir), ManifestFile);
        var baselinePath = Path.Combine(runDir, BaselineFile);
        if (File.Exists(baselinePath) || !File.Exists(manifestPath)) return;
        var manifest = JsonSerializer.Deserialize<ManifestDoc>(await File.ReadAllTextAsync(manifestPath, ct))!;

        var baseline = new SortedDictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var repo in manifest.files.Select(f => f.repo).Distinct().Order(StringComparer.Ordinal))
        {
            var git = GitFor(svc, repo);
            var integration = git.IntegrationPath(runId);
            var target = Path.Combine(integration, Codegen.area);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true); // a removed contract's old files go
            var source = Path.Combine(WorkDir(runDir), Codegen.outPath(repo, Codegen.area));
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
            }
            await git.CommitPathsAsync(integration, [Codegen.area], $"factory: seed generated contracts for {runId}", ct);
            baseline[repo] = Hashes(integration);
        }
        await File.WriteAllTextAsync(baselinePath, JsonSerializer.Serialize(baseline), ct);
    }

    /// Repo-relative path to sha256 of every file under `generated/contracts/`.
    public static SortedDictionary<string, string> Hashes(string integration)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var dir = Path.Combine(integration, Codegen.area);
        if (Directory.Exists(dir))
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                map[Path.GetRelativePath(integration, f).Replace('\\', '/')] = UiPreview.Sha256Hex(File.ReadAllBytes(f));
        return map;
    }

    private static Dictionary<string, Dictionary<string, string>>? Baseline(string runDir) =>
        File.Exists(Path.Combine(runDir, BaselineFile))
            ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(Path.Combine(runDir, BaselineFile)))
            : null;

    /// Per repo, the lines naming each generated file that is missing, modified or added since seeding; empty when nothing moved or nothing was seeded.
    public static IReadOnlyList<(string Repo, IReadOnlyList<string> Drift)> Drift(FactoryServices svc, string runId)
    {
        if (Baseline(svc.Settings.RunDir(runId)) is not { } baseline) return [];
        var found = new List<(string, IReadOnlyList<string>)>();
        foreach (var (repo, hashes) in baseline.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            var current = Hashes(GitFor(svc, repo).IntegrationPath(runId));
            var drift = Codegen.drift(ToMap(hashes), ToMap(current));
            if (!drift.IsEmpty) found.Add((repo, [.. drift]));
        }
        return found;
    }

    /// Seeded files of `repo` whose bytes still equal the baseline: the Factory's own, so not a change the merge judges.
    public static HashSet<string> UnchangedSeeded(string integration, string runDir, string repo)
    {
        if (Baseline(runDir) is not { } baseline || !baseline.TryGetValue(repo, out var hashes)) return [];
        var now = Hashes(integration);
        return [.. hashes.Where(kv => now.TryGetValue(kv.Key, out var h) && h == kv.Value).Select(kv => kv.Key)];
    }

    private static FSharpMap<string, string> ToMap(IEnumerable<KeyValuePair<string, string>> kvs) =>
        MapModule.OfSeq(kvs.Select(kv => Tuple.Create(kv.Key, kv.Value)));
}
