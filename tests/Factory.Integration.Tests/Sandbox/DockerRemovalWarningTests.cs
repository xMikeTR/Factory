using Factory.Harness.Sandbox;
using Xunit;

namespace Factory.Integration.Tests.Sandbox;

public class DockerRemovalWarningTests
{
    [Fact]
    public void The_warning_names_the_container_the_command_that_removes_it_and_dockers_first_line()
    {
        var w = DockerSandbox.RemovalWarning("s2-team-1", "docker rm -f s2-team-1 failed:\nCannot connect to the Docker daemon\nmore");

        Assert.Contains("s2-team-1", w);
        Assert.Contains($"`{ContainerEngine.Name} rm -f s2-team-1`", w);
        Assert.Contains("failed:", w);
        Assert.DoesNotContain("Cannot connect", w);
    }
}
