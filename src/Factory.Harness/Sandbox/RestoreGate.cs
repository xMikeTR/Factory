using System.Text.RegularExpressions;
using Factory.Core;

namespace Factory.Harness.Sandbox;

/// What a restore window restores, and the changes that stop it before the network opens.
/// Projects: every project file on disk (live5: a project outside the sln was never restored).
/// Refusals: changes since HEAD to the files `dotnet restore` reads that are off the
/// ProjectFileChange allow-list, and project paths the restore command cannot carry safely.
public sealed record RestorePlan(IReadOnlyList<string> Projects, IReadOnlyList<string> Refusals, IReadOnlyList<string>? NpmDirectories = null)
{
    /// Directories whose package.json and lockfile `npm ci` restores.
    public IReadOnlyList<string> NpmDirs => NpmDirectories ?? [];
}

public static partial class RestoreGate
{
    public const string ConfigFile = "/etc/factory/nuget.config";
    public const string NpmConfigFile = "/etc/factory/npmrc";
    public const string NuGetOrg = "https://api.nuget.org/v3/index.json";
    private static readonly string[] Skipped = ["bin", "obj", "node_modules"];

    /// HEAD is trusted: every commit the factory makes in a worktree follows a gated restore of
    /// exactly that content, and the base is what the repo's owners merged.
    public static async Task<RestorePlan> PlanAsync(string worktree, CancellationToken ct)
    {
        var projects = new List<string>();
        var refusals = new List<string>();
        var npm = new List<string>();
        foreach (var path in Inputs(worktree, worktree))
        {
            var before = await HeadAsync(worktree, path, ct);
            if (Factory.Core.Core.ReviewRestoreInput(path, before, await File.ReadAllTextAsync(Path.Combine(worktree, path), ct)) is { IsEmpty: false } errors)
            {
                refusals.AddRange(errors);
                continue;
            }
            if (Path.GetFileName(path) == "package.json")
            {
                var dir = Path.GetDirectoryName(path)!.Replace('\\', '/');
                if (!File.Exists(Path.Combine(worktree, dir, "package-lock.json")))
                    refusals.Add($"{path}: no package-lock.json next to it; npm ci restores only from a lockfile (nothing else pins what would install)");
                else if (dir.Length > 0 && !SafePath().IsMatch(dir))
                    refusals.Add($"{path}: the directory has characters the restore command does not carry (letters, digits, . _ - + / only)");
                else npm.Add(dir.Length == 0 ? "." : dir);
                continue;
            }
            if (!ProjectFileChange.isProject(Path.GetFileName(path))) continue;
            if (SafePath().IsMatch(path)) projects.Add(path);
            else refusals.Add($"{path}: the project path has characters the restore command does not carry (letters, digits, . _ - + / only)");
        }
        return new RestorePlan(projects, refusals, npm);
    }

    /// One restore per project, sources pinned: the image's config lists nuget.org only, and the
    /// properties override any source a project could name. Project extension imports are off:
    /// a package restored earlier must not run its targets in a restore that has network.
    public static string Command(IReadOnlyList<string> projects) =>
        $"rc=0; for p in {string.Join(' ', projects.Select(p => $"'{p}'"))}; do "
        + $"dotnet restore \"$p\" --configfile {ConfigFile} -p:RestoreSources={NuGetOrg} -p:RestoreAdditionalProjectSources= "
        + "-p:ImportProjectExtensionProps=false -p:ImportProjectExtensionTargets=false || rc=1; done; exit $rc";

    /// One `npm ci` per directory: lockfile only, no install scripts, the registry from the image's config.
    public static string NpmCommand(IReadOnlyList<string> directories) =>
        $"rc=0; for d in {string.Join(' ', directories.Select(d => $"'{d}'"))}; do "
        + $"(cd \"$d\" && npm ci --ignore-scripts --no-audit --no-fund --userconfig {NpmConfigFile}) || rc=1; done; exit $rc";

    /// After the restore, offline: only the allow-listed packages run their install script. Names are validated by NpmPolicyFile.
    public static string RebuildCommand(string directory, IReadOnlyList<string> packages) =>
        $"cd '{directory}' && npm rebuild {string.Join(' ', packages.Select(p => $"'{p}'"))} --offline --userconfig {NpmConfigFile}";

    /// Packages the lockfile says have an install script that `--ignore-scripts` skipped and the policy did not allow:
    /// reported, so a build that needs one is a decision for the repo's owners, not a silent gap.
    public static IReadOnlyList<string> SkippedInstallScripts(string lockfileJson, IReadOnlyList<string> allowed)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(lockfileJson);
        if (!doc.RootElement.TryGetProperty("packages", out var packages)) return [];
        return packages.EnumerateObject()
            .Where(p => p.Value.ValueKind == System.Text.Json.JsonValueKind.Object
                && p.Value.TryGetProperty("hasInstallScript", out var h) && h.ValueKind == System.Text.Json.JsonValueKind.True)
            // A workspace package has no node_modules/ in its key: it is named by its path.
            .Select(p => p.Name.LastIndexOf("node_modules/", StringComparison.Ordinal) is var at and >= 0 ? p.Name[(at + "node_modules/".Length)..] : p.Name)
            .Where(n => !allowed.Contains(n)).ToList();
    }

    /// One offline rebuild per npm directory; none unless the policy names packages.
    public static IReadOnlyList<string> RebuildCommands(IReadOnlyList<string> directories, IReadOnlyList<string> packages) =>
        packages.Count == 0 ? [] : directories.Select(d => RebuildCommand(d, packages)).ToList();

    /// "dir: package" for each install script the restore skipped and the policy did not allow, read from the directories' lockfiles.
    public static async Task<IReadOnlyList<string>> SkippedInstallScriptsAsync(
        string worktree, IReadOnlyList<string> directories, IReadOnlyList<string> allowed, CancellationToken ct)
    {
        var skipped = new List<string>();
        foreach (var dir in directories)
            skipped.AddRange(SkippedInstallScripts(await File.ReadAllTextAsync(Path.Combine(worktree, dir, "package-lock.json"), ct), allowed)
                .Select(p => $"{dir}: {p}"));
        return skipped;
    }

    /// Every restore the plan needs, as one command for the restore window.
    public static string FullCommand(RestorePlan plan) =>
        string.Join(" && ", new[] { plan.Projects.Count > 0 ? "(" + Command(plan.Projects) + ")" : null, plan.NpmDirs.Count > 0 ? "(" + NpmCommand(plan.NpmDirs) + ")" : null }.OfType<string>());

    private static IEnumerable<string> Inputs(string root, string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir).Order(StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (ProjectFileChange.affectsRestore(rel) || PackageJsonChange.affects(rel)) yield return rel;
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
