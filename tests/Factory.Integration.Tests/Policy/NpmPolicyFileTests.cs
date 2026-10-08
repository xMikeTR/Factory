using Factory.Core;
using Factory.Harness.Policy;
using Factory.Harness.Sandbox;
using Xunit;

namespace Factory.Integration.Tests.Policy;

public class NpmPolicyFileTests
{
    private static string RepoWith(string? yaml)
    {
        var repo = Path.Combine(Path.GetTempPath(), "s2-npm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repo, ".factory"));
        if (yaml is not null) File.WriteAllText(Path.Combine(repo, ".factory", "policy.yaml"), yaml);
        return repo;
    }

    [Fact]
    public void No_npm_section_allows_no_install_script()
    {
        Assert.Empty(NpmPolicyFile.Load(RepoWith(null)).Rebuild);
    }

    [Fact]
    public void The_file_lists_the_packages_whose_install_script_may_run_after_the_restore()
    {
        var p = NpmPolicyFile.Load(RepoWith("npm:\n  rebuild:\n    - esbuild\n    - '@swc/core'\n"));

        Assert.Equal(["esbuild", "@swc/core"], p.Rebuild);
    }

    [Fact]
    public void A_name_the_shell_command_could_not_carry_is_rejected()
    {
        Assert.Throws<FactoryRejectedException>(() => NpmPolicyFile.Load(RepoWith("npm:\n  rebuild:\n    - 'x; curl evil'\n")));
    }

    [Fact]
    public void The_rebuild_runs_offline_only_for_the_listed_packages()
    {
        var command = RestoreGate.RebuildCommand("web", ["esbuild"]);

        Assert.Contains("cd 'web'", command);
        Assert.Contains("npm rebuild 'esbuild'", command);
        Assert.Contains("--offline", command);
    }

    [Fact]
    public void Packages_whose_install_script_was_skipped_and_not_allowed_are_reported()
    {
        const string lockfile = """
            { "packages": { "": {}, "node_modules/esbuild": { "hasInstallScript": true },
              "node_modules/@scope/native": { "hasInstallScript": true }, "node_modules/plain": {} } }
            """;

        Assert.Equal(["@scope/native"], RestoreGate.SkippedInstallScripts(lockfile, ["esbuild"]));
    }
}
