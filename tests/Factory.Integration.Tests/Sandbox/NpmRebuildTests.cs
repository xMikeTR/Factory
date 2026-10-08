using Factory.Harness.Sandbox;
using Xunit;

namespace Factory.Integration.Tests.Sandbox;

public class NpmRebuildTests
{
    private const string Lock = """{"packages":{"":{},"node_modules/esbuild":{"hasInstallScript":true},"node_modules/a/node_modules/sharp":{"hasInstallScript":true},"node_modules/left-pad":{}}}""";

    private static string Repo(string dir, string lockJson)
    {
        var root = Directory.CreateTempSubdirectory("npm").FullName;
        Directory.CreateDirectory(Path.Combine(root, dir));
        File.WriteAllText(Path.Combine(root, dir, "package-lock.json"), lockJson);
        return root;
    }

    [Fact]
    public async Task Skipped_scripts_name_the_directory_and_package_the_policy_did_not_allow()
    {
        var root = Repo("web", Lock);
        var skipped = await RestoreGate.SkippedInstallScriptsAsync(root, ["web"], ["esbuild"], CancellationToken.None);
        Assert.Equal(["web: sharp"], skipped);
    }

    [Fact]
    public async Task Nothing_is_skipped_when_every_script_is_allowed()
    {
        var root = Repo(".", Lock);
        Assert.Empty(await RestoreGate.SkippedInstallScriptsAsync(root, ["."], ["esbuild", "sharp"], CancellationToken.None));
    }

    [Fact]
    public void Rebuild_commands_exist_per_directory_only_when_the_policy_names_packages()
    {
        Assert.Empty(RestoreGate.RebuildCommands(["web"], []));
        var cmd = Assert.Single(RestoreGate.RebuildCommands(["web"], ["esbuild"]));
        Assert.Equal(RestoreGate.RebuildCommand("web", ["esbuild"]), cmd);
    }
}
