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
/// in `{runDir}/codegen/out/<repo>/`. After the integration branch exists they are seeded into `generated/contracts/csharp/`, where
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
            return null;
        }
        if (File.Exists(manifestPath)) // resume: what was generated stays
        {
            await svc.LogAsync(runId, Stage, LedgerKinds.Codegen, outcome: "reused", payload: await File.ReadAllTextAsync(manifestPath, ct), ct: ct);
            return null;
        }

        var names = svc.Settings.CodegenCsharp;
        if (names is not null && CodegenPolicyFile.Unknown(names, svc.Settings.RepoNames) is { Count: > 0 } unknown)
            throw new FactoryRejectedException(".factory/policy.yaml codegen is invalid", [.. unknown]);
        var targets = Codegen.targets(handover, names is null ? FSharpOption<FSharpList<string>>.None : FSharpOption<FSharpList<string>>.Some(ListModule.OfSeq(names)));
        if (targets.IsError) throw new FactoryRejectedException("contracts cannot be generated", [.. targets.ErrorValue]);
        var list = targets.ResultValue.ToList();
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
            foreach (var f in list.SelectMany(t => t.Files.Select(f => (t, f))).Where(x => !File.Exists(Path.Combine(work, Codegen.outPath(x.t.Repo, x.f)))))
                run = new CodegenRun("failed", $"the generator reported success but did not write {Codegen.outPath(f.t.Repo, f.f)}", run.Commands);
        if (run.Outcome != "generated")
        {
            await svc.LogAsync(runId, Stage, LedgerKinds.Codegen, outcome: run.Outcome,
                payload: JsonSerializer.Serialize(new { detail = run.Detail, commands = run.Commands }), ct: ct);
            return new RunFinished(runId, "codegen_failed", run.Detail);
        }

        var manifest = Manifest(list, work, handover);
        await File.WriteAllTextAsync(manifestPath, manifest, ct);
        await svc.LogAsync(runId, Stage, LedgerKinds.Codegen, outcome: "generated", payload: manifest, ct: ct);
        return null;
    }

    /// `{tool, version, files: [{repo, path, sha256, source, source_sha256}]}`, sorted: the same inputs give the same bytes.
    private static string Manifest(List<Codegen.Target> targets, string work, Handover handover)
    {
        var sources = handover.ApiContractTexts.ToDictionary(t => t.Path, t => t.Sha256, StringComparer.Ordinal);
        var files = targets
            .SelectMany(t => t.Files.Select(f => new
            {
                repo = t.Repo,
                path = f,
                sha256 = UiPreview.Sha256Hex(File.ReadAllBytes(Path.Combine(work, Codegen.outPath(t.Repo, f)))),
                source = t.Source,
                source_sha256 = sources[t.Source],
            }))
            .OrderBy(f => f.repo, StringComparer.Ordinal).ThenBy(f => f.path, StringComparer.Ordinal).ToList();
        return JsonSerializer.Serialize(new { tool = "nswag", version = Codegen.nswagVersion, files });
    }

    private sealed record ManifestFileRow(string repo, string path);
    private sealed record ManifestDoc(List<ManifestFileRow> files);

    private static GitWorkspace GitFor(FactoryServices svc, string repo) =>
        repo != "." && svc.RepoGits.TryGetValue(repo, out var g) ? g : svc.Git;

    /// Once per run, after the integration branches exist: replace `generated/contracts/csharp/` with the generated files and commit
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
            var target = Path.Combine(integration, Codegen.root);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true); // a removed contract's old files go
            var source = Path.Combine(WorkDir(runDir), Codegen.outPath(repo, Codegen.root));
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
            }
            await git.CommitPathsAsync(integration, [Codegen.root], $"factory: seed generated contracts for {runId}", ct);
            baseline[repo] = Hashes(integration);
        }
        await File.WriteAllTextAsync(baselinePath, JsonSerializer.Serialize(baseline), ct);
    }

    /// Repo-relative path to sha256 of every file under `generated/contracts/csharp/`.
    public static SortedDictionary<string, string> Hashes(string integration)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var dir = Path.Combine(integration, Codegen.root);
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
