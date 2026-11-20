using System.Text.RegularExpressions;
using Factory.Core;
using Xunit;

namespace Factory.Integration.Tests.Sandbox;

/// The runner image and the code that checks `nswag version` must name the same NSwag.
public class RunnerImagePinTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Factory.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Factory.sln not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void The_Dockerfile_pins_the_NSwag_version_the_codegen_phase_expects()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "docker", "runner", "Dockerfile"));
        var arg = Regex.Match(text, @"^ARG NSWAG_VERSION=(\S+)\s*$", RegexOptions.Multiline);
        Assert.True(arg.Success, "docker/runner/Dockerfile has no ARG NSWAG_VERSION=");
        Assert.Equal(Codegen.nswagVersion, arg.Groups[1].Value);
    }
}
