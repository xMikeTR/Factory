using Factory.Core;
using Factory.Harness.Registry;

namespace Factory.S2;

using Core = Factory.Core.Core; // "Core" alone binds to the namespace Factory.Core from inside Factory.*

/// What `run --handover` accepts: a frozen Designc handover directory (read, hash-checked and
/// mapped by DesigncHandoverReader) or an already-mapped handover JSON file.
public static class HandoverInput
{
    /// Returns the path of the handover JSON a run starts from. A directory is frozen to
    /// {stateDir}/handovers/{dirname}.json; a file is returned unchanged.
    public static async Task<string> ResolveAsync(string handoverPath, string stateDir, CancellationToken ct)
    {
        if (!Directory.Exists(handoverPath)) return handoverPath;

        var handover = await new DesigncHandoverReader().LoadAsync(handoverPath, S2Config.Default, ct);
        var dir = Path.Combine(stateDir, "handovers");
        Directory.CreateDirectory(dir);
        var json = Path.Combine(dir, Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(handoverPath))) + ".json");
        await File.WriteAllTextAsync(json, Core.HandoverJson(handover), ct);
        if (!handover.Ui.IsEmpty) await CopyUiPreviewsAsync(handover, handoverPath, PreviewsFolder(json), ct);
        return json;
    }

    /// The approved preview pictures of a frozen handover JSON live beside it: `<name>.json` has `<name>.ui/previews/<screen>/**`.
    /// The handover directory is gone once the run starts, and only the JSON reaches it, so the pictures are copied here.
    public static string PreviewsFolder(string handoverJson) =>
        Path.Combine(Path.ChangeExtension(handoverJson, null) + ".ui", "previews");

    /// Copies `ui/previews/<screen>/**` out of the handover directory, refusing anything that is not exactly the set `ui.json` names:
    /// a missing file, an extra file or a file whose sha256 differs. Nothing is left behind on a refusal.
    public static async Task CopyUiPreviewsAsync(Handover handover, string handoverDir, string target, CancellationToken ct)
    {
        var problems = new List<string>();
        var staged = new List<(string From, string To)>();
        foreach (var screen in handover.Ui)
        {
            var source = Path.Combine(handoverDir, "ui", "previews", screen.Screen);
            var named = screen.Previews.ToDictionary(p => p.Item1, p => p.Item2, StringComparer.Ordinal);
            var present = Directory.Exists(source)
                ? Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(source, f).Replace('\\', '/')).ToHashSet(StringComparer.Ordinal)
                : [];
            foreach (var rel in named.Keys.Except(present).Order(StringComparer.Ordinal)) problems.Add($"ui/previews/{screen.Screen}/{rel} is named in ui.json but missing");
            foreach (var rel in present.Except(named.Keys).Order(StringComparer.Ordinal)) problems.Add($"ui/previews/{screen.Screen}/{rel} is not named in ui.json");
            foreach (var (rel, sha) in named.Where(n => present.Contains(n.Key)))
            {
                var from = Path.Combine(source, rel);
                if (UiPreview.Sha256Hex(await File.ReadAllBytesAsync(from, ct)) != sha) problems.Add($"ui/previews/{screen.Screen}/{rel} does not match the sha256 in ui.json (changed after the approval)");
                else staged.Add((from, Path.Combine(target, screen.Screen, rel)));
            }
        }
        if (problems.Count > 0) throw new FactoryRejectedException("the approved previews in the handover cannot be trusted", problems.ToArray());
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        foreach (var (from, to) in staged)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to, overwrite: true);
        }
    }

    /// `run` start: puts the previews beside the run's handover.json (`{runDir}/ui/previews`), if absent like handover.json itself.
    /// A handover with screens whose pictures never arrived is refused: nothing could be judged against them.
    public static void CopyUiPreviewsToRun(Handover handover, string handoverJson, string runDir)
    {
        if (handover.Ui.IsEmpty) return;
        var target = Path.Combine(runDir, "ui", "previews");
        if (Directory.Exists(target)) return;
        var source = PreviewsFolder(handoverJson);
        if (!Directory.Exists(source))
            throw new FactoryRejectedException("the handover has UI screens but no approved previews beside it",
                [$"expected {source}; freeze the handover directory with 'run --handover <dir>' so the pictures are copied and checked"]);
        CopyTree(source, target);
    }

    private static void CopyTree(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }
}
