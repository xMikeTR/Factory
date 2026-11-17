using Factory.Core;
using Factory.Harness.Sandbox;
using Xunit;

namespace Factory.Integration.Tests.Sandbox;

/// dotnet-affected is in the runner image and answers from `--assume-changes` without a git repository (only the worktree is mounted in
/// the sandbox, so `--from` cannot open the repo). Opt-in: FACTORY_TEST_DOCKER=1.
[Trait("needs", "docker")]
public class AffectedDockerTests
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("FACTORY_TEST_DOCKER") == "1";

    [Fact]
    public async Task The_image_names_the_test_project_a_changed_library_reaches()
    {
        if (!Enabled) return;
        var dir = Directory.CreateTempSubdirectory("affected").FullName;
        Directory.CreateDirectory(Path.Combine(dir, "src", "Lib"));
        Directory.CreateDirectory(Path.Combine(dir, "tests", "Lib.Tests"));
        File.WriteAllText(Path.Combine(dir, "src", "Lib", "Lib.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(dir, "tests", "Lib.Tests", "Lib.Tests.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../../src/Lib/Lib.csproj\" /></ItemGroup></Project>");
        await using var sandbox = await new DockerSandboxFactory(new DockerSandboxOptions()).ForTeamAsync("affected", dir, default);

        var r = await sandbox.RunAsync(Affected.dotnetCommand(Microsoft.FSharp.Collections.ListModule.OfSeq(["src/Lib/Lib.csproj"])), dir, TimeSpan.FromMinutes(2), new Dictionary<string, string>(), default);

        var parsed = Affected.parseDotnet(r.ExitCode, r.Output);
        Assert.True(parsed.IsOk, r.Tail(2000));
        Assert.Contains(parsed.ResultValue, p => p.EndsWith("Lib.Tests.csproj", StringComparison.Ordinal));
    }
}
