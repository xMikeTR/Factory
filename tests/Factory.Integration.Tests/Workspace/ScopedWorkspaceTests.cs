using Factory.Harness;
using Factory.Harness.Sandbox;
using Xunit;

namespace Factory.Integration.Tests.Workspace;

public class ScopedWorkspaceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ws-").FullName;

    [Fact]
    public async Task Commands_run_in_the_sandbox_without_host_secrets()
    {
        Environment.SetEnvironmentVariable("FACTORY_GATEWAY_KEY", "sekrit-gateway");
        try
        {
            var ws = new ScopedWorkspace(_root, [], [], ["printenv"], new TurnSignals(), new LocalSandbox());
            var output = await ws.RunCommand("printenv");
            Assert.StartsWith("exit 0", output);
            Assert.DoesNotContain("sekrit-gateway", output);
            Assert.Contains("PATH=", output);
        }
        finally { Environment.SetEnvironmentVariable("FACTORY_GATEWAY_KEY", null); }
    }

    [Fact]
    public async Task Without_a_sandbox_commands_are_refused()
    {
        var ws = new ScopedWorkspace(_root, [], [], ["printenv"], new TurnSignals());
        Assert.StartsWith("DENIED", await ws.RunCommand("printenv"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
