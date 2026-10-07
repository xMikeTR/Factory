using System.Text.RegularExpressions;
using Factory.Core;

namespace Factory.Harness.Sandbox;

/// What a restore window restores, and the changes that stop it before the network opens.
/// Projects: every project file on disk (live5: a project outside the sln was never restored).
/// Refusals: changes since HEAD to the files `dotnet restore` reads that are off the
/// ProjectFileChange allow-list, and project paths the restore command cannot carry safely.
public sealed record RestorePlan(IReadOnlyList<string> Projects, IReadOnlyList<string> Refusals);

public static partial class RestoreGate
{
    public const string ConfigFile = "/etc/factory/nuget.config";
    public const string NuGetOrg = "https://api.nuget.org/v3/index.json";
    private static readonly string[] Skipped = ["bin", "obj", "node_modules"];

    /// HEAD is trusted: every commit the factory makes in a worktree follows a gated restore of
    /// exactly that content, and the base is what the repo's owners merged.
    public static async Task<RestorePlan> PlanAsync(string worktree, CancellationToken ct)
    {
        var projects = new List<string>();
        var refusals = new List<string>();
        foreach (var path in Inputs(worktree, worktree))
        {
            var before = await HeadAsync(worktree, path, ct);
            if (Factory.Core.Core.ReviewRestoreInput(path, before, await File.ReadAllTextAsync(Path.Combine(worktree, path), ct)) is { IsEmpty: false } errors)
            {
                refusals.AddRange(errors);
                continue;
            }
            if (!ProjectFileChange.isProject(Path.GetFileName(path))) continue;
            if (SafePath().IsMatch(path)) projects.Add(path);
            else refusals.Add($"{path}: the project path has characters the restore command does not carry (letters, digits, . _ - + / only)");
        }
        return new RestorePlan(projects, refusals);
    }

    /// One restore per project, sources pinned: the image's config lists nuget.org only, and the
    /// properties override any source a project could name. Project extension imports are off:
    /// a package restored earlier must not run its targets in a restore that has network.
    public static string Command(IReadOnlyList<string> projects) =>
        $"rc=0; for p in {string.Join(' ', projects.Select(p => $"'{p}'"))}; do "
        + $"dotnet restore \"$p\" --configfile {ConfigFile} -p:RestoreSources={NuGetOrg} -p:RestoreAdditionalProjectSources= "
        + "-p:ImportProjectExtensionProps=false -p:ImportProjectExtensionTargets=false || rc=1; done; exit $rc";

    private static IEnumerable<string> Inputs(string root, string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir).Order(StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (ProjectFileChange.affectsRestore(rel)) yield return rel;
        }
        foreach (var sub in Directory.EnumerateDirectories(dir).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(sub);
            if (name.StartsWith('.') || Skipped.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            foreach (var rel in Inputs(root, sub)) yield return rel;
        }
    }

    /// The file as HEAD has it; null when it is new, or the worktree is not a git checkout.
    private static async Task<string?> HeadAsync(string worktree, string path, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync("git", ["show", $"HEAD:{path}"], worktree, TimeSpan.FromSeconds(30), ct: ct);
        return r.Succeeded ? r.Output : null;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_+][A-Za-z0-9._+/-]*$")]
    private static partial Regex SafePath();
}
