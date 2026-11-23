using Factory.Harness;
using Xunit;

namespace Factory.Integration.Tests.Verify;

/// The fixture app as a committed git repo, and a copy of the fixture registry, both in temp folders.
public sealed record UiPreviewFixture(string Repo, string Registry)
{
    public static string FixtureRoot => Path.Combine(AppContext.BaseDirectory, "Fixtures", "ui-preview-app");

    /// A real designc checkout (FACTORY_DESIGNC_DIR, or Designc/app beside System2/ up the tree), or null: the tests that run designc's python then do nothing.
    public static string? DesigncDir()
    {
        if (Environment.GetEnvironmentVariable("FACTORY_DESIGNC_DIR") is { Length: > 0 } d) return Directory.Exists(Path.Combine(d, "python", "designc")) ? d : null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Designc", "app");
            if (Directory.Exists(Path.Combine(candidate, "python", "designc"))) return candidate;
        }
        return null;
    }

    public static async Task<UiPreviewFixture> CreateAsync()
    {
        var root = Directory.CreateTempSubdirectory("ui-preview-fixture").FullName;
        var repo = Path.Combine(root, "app");
        var registry = Path.Combine(root, "registry");
        Copy(Path.Combine(FixtureRoot, "app"), repo);
        Copy(Path.Combine(FixtureRoot, "registry"), registry);
        await Git(repo, "init", "-q", "-b", "main");
        await Git(repo, "config", "user.name", "factory-test");
        await Git(repo, "config", "user.email", "factory-test@localhost");
        await Git(repo, "add", "-A");
        await Git(repo, "commit", "-qm", "app");
        return new(repo, registry);
    }

    public static async Task Git(string dir, params string[] args) =>
        Assert.True((await ProcessRunner.RunAsync("git", args, dir, TimeSpan.FromSeconds(30))).Succeeded, "git " + string.Join(' ', args));

    public static void Copy(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(dir.Replace(from, to));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories)) File.Copy(file, file.Replace(from, to), overwrite: true);
    }
}
