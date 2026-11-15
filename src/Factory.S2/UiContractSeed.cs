using System.Text.Json;
using Factory.Core;
using Factory.Harness.Registry;
using Factory.S2.Git;
using Microsoft.FSharp.Collections;

namespace Factory.S2;

/// The ui: check's inputs, put on the integration branch once per run by the Factory (docs/ui-contract-verify.md §2): each screen's
/// spec, the component manifests, the approved pictures and the page test. The coding agent never writes them, and a later change to
/// any of them (or to the Vitest config or the preview helper they run through) is drift, caught before a check runs.
public static class UiContractSeed
{
    private const string BaselineFile = "ui-baseline.json";

    public static async Task SeedAsync(GitWorkspace git, string runId, string integration, Handover handover, string runDir, CancellationToken ct)
    {
        if (handover.Ui.IsEmpty || File.Exists(Path.Combine(runDir, BaselineFile))) return;
        var contract = Path.Combine(integration, "ui-contract");
        var written = new List<string>();
        async Task Put(string rel, string text)
        {
            var full = Path.Combine(integration, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, text, ct);
            written.Add(rel);
        }

        foreach (var screen in handover.Ui)
        {
            await Put($"ui-contract/specs/{screen.Screen}.json", screen.Spec.Text);
            foreach (var c in screen.Components)
                await Put($"ui-contract/manifests/{Path.GetFileName(c.Path)}", c.Text);
            await Put($"ui-contract/tests/{screen.Screen}.page.test.tsx",
                UiPageTest.text(screen.Screen, screen.Component, screen.File, ListModule.OfSeq(UiSpecStates.ofSpec(screen.Spec.Text))));

            // The approved folder replaces whatever the repo held, so a stale picture cannot survive.
            var source = Path.Combine(runDir, "ui", "previews", screen.Screen);
            var target = Path.Combine(contract, "previews", screen.Screen);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            if (Directory.Exists(source))
                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(contract, Path.Combine(target, Path.GetRelativePath(source, file))).Replace('\\', '/');
                    var dest = Path.Combine(contract, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(file, dest, overwrite: true);
                    written.Add("ui-contract/" + rel);
                }
        }
        // The previews folder may also hold files the repo had committed for these screens: stage the whole folders.
        var folders = handover.Ui.Select(s => $"ui-contract/previews/{s.Screen}").ToList();
        await git.CommitPathsAsync(integration, [.. written.Where(w => !w.StartsWith("ui-contract/previews/")), .. folders],
            $"factory: seed ui contract for {runId}", ct);

        var baseline = Hashes(integration);
        await File.WriteAllTextAsync(Path.Combine(runDir, BaselineFile), JsonSerializer.Serialize(baseline), ct);
    }

    /// Repo-relative path to sha256 of every file the check relies on.
    public static SortedDictionary<string, string> Hashes(string integration)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        void Add(string full) => map[Path.GetRelativePath(integration, full).Replace('\\', '/')] =
            UiPreview.Sha256Hex(File.ReadAllBytes(full));
        foreach (var dir in new[] { "specs", "manifests", "previews", "tests", "preview" })
        {
            var d = Path.Combine(integration, "ui-contract", dir);
            if (Directory.Exists(d)) foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)) Add(f);
        }
        foreach (var f in Directory.EnumerateFiles(integration, "vitest.config.*", SearchOption.TopDirectoryOnly)) Add(f);
        return map;
    }

    /// Lines naming each modified, missing or added (previews only) file; empty when no baseline was recorded or nothing moved.
    public static async Task<IReadOnlyList<string>> DriftAsync(string integration, string runDir, CancellationToken ct)
    {
        var path = Path.Combine(runDir, BaselineFile);
        if (!File.Exists(path)) return [];
        var baseline = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(path, ct))!;
        return [.. UiBaseline.drift(MapModule.OfSeq(baseline.Select(kv => Tuple.Create(kv.Key, kv.Value))),
            MapModule.OfSeq(Hashes(integration).Select(kv => Tuple.Create(kv.Key, kv.Value))))];
    }
}
