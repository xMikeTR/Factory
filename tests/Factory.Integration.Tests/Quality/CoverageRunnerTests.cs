using Factory.Harness;
using Factory.Harness.Sandbox;
using Factory.S2.Quality;
using Xunit;

namespace Factory.Integration.Tests.Quality;

public class CoverageRunnerTests
{
    private sealed class FakeSandbox(Func<string, ProcessResult> answer) : ICommandSandbox
    {
        public List<string> Commands { get; } = [];
        public string Kind => "fake";
        public Task<ProcessResult> RunAsync(string command, string workdir, TimeSpan timeout, IReadOnlyDictionary<string, string> env, CancellationToken ct)
        {
            Commands.Add(command);
            return Task.FromResult(answer(command));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string Worktree()
    {
        var root = Directory.CreateTempSubdirectory("cov").FullName;
        Directory.CreateDirectory(Path.Combine(root, "tests"));
        File.WriteAllText(Path.Combine(root, "tests/T.csproj"), "<Project><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"1\" /></ItemGroup></Project>");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src/Lib.csproj"), "<Project />");
        return root;
    }

    private static ProcessResult Ok(string o = "") => new(0, o, TimeSpan.Zero, false);

    [Fact]
    public async Task Builds_and_runs_each_test_project_under_coverlet_and_returns_the_reports()
    {
        var sandbox = new FakeSandbox(c => c.StartsWith("cat ") ? Ok("<coverage/>") : Ok());
        var reports = await CoverageRunner.CollectAsync(sandbox, Worktree(), default);

        Assert.Equal(["<coverage/>"], reports);
        Assert.Single(sandbox.Commands, c => c.Contains("coverlet") && c.Contains("vstest") && c.Contains("T.dll") && c.Contains("--format cobertura"));
        Assert.Single(sandbox.Commands, c => c.Contains("dotnet build 'tests/T.csproj'") && c.Contains("--no-restore"));
        Assert.DoesNotContain(sandbox.Commands, c => c.Contains("Lib.csproj"));
    }

    [Fact]
    public async Task A_project_that_does_not_build_or_run_yields_no_report_and_nothing_throws()
    {
        var sandbox = new FakeSandbox(_ => new ProcessResult(1, "boom", TimeSpan.Zero, false));
        Assert.Empty(await CoverageRunner.CollectAsync(sandbox, Worktree(), default));
    }
}
