using Factory.Harness.Sandbox;
using Xunit;

namespace Factory.Integration.Tests.Sandbox;

/// Decision checks run in the registry inside the runner image. A registry's Python checks read the model
/// through designc's own parser, so the image carries python3 with designc on it, offline, and never needs
/// the registry's own .venv (whose python points at the host). Opt-in: FACTORY_TEST_DOCKER=1.
[Trait("needs", "docker")]
public class RegistryCheckToolsDockerTests
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("FACTORY_TEST_DOCKER") == "1";

    [Fact]
    public async Task A_registry_python_check_runs_with_designc_offline()
    {
        if (!Enabled) return;
        var registry = Directory.CreateTempSubdirectory("registry-check").FullName;
        Directory.CreateDirectory(Path.Combine(registry, "checks"));
        File.WriteAllText(Path.Combine(registry, "checks", "model.py"),
            "import sys, yaml\nfrom designc import dsl\nsys.exit(0 if callable(dsl.parse) else 1)\n");
        await using var sandbox = await new DockerSandboxFactory(new DockerSandboxOptions()).ForTeamAsync("integration", registry, default);

        var r = await sandbox.RunAsync("python3 checks/model.py", registry, TimeSpan.FromMinutes(1), new Dictionary<string, string>(), default);

        Assert.True(r.Succeeded, r.Tail(2000));
    }
}
