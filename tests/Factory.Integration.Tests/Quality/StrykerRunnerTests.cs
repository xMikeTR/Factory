using Xunit;
using Factory.Core;
using Factory.Harness;
using Factory.Harness.Sandbox;
using Factory.S2.Quality;

namespace Factory.Integration.Tests.Quality;

public class StrykerRunnerTests
{
    private const string Report = """{"files":{"/w/src/A.cs":{"mutants":[{"id":"1","mutatorName":"Block","status":"Killed","location":{"start":{"line":3}}}]}}}""";

    /// A sandbox that plays Stryker: records commands, answers the report read.
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

    private sealed class Sandboxes(FakeSandbox sandbox) : ISandboxFactory
    {
        public Task<ICommandSandbox> ForTeamAsync(string teamId, string worktree, CancellationToken ct) => Task.FromResult<ICommandSandbox>(sandbox);
    }

    private static ProcessResult Ok(string output = "") => new(0, output, TimeSpan.Zero, false);

    private static string Worktree()
    {
        var dir = Directory.CreateTempSubdirectory("stryker").FullName;
        File.WriteAllText(Path.Combine(dir, "App.sln"), "");
        return dir;
    }

    [Fact]
    public async Task Mutates_only_the_given_files_of_the_solution_and_reads_the_json_report()
    {
        var wt = Worktree();
        var sandbox = new FakeSandbox(c => c.StartsWith("cat ") ? Ok(Report.Replace("/w/", wt + "/")) : Ok());
        var run = await new StrykerRunner(new Sandboxes(sandbox)).RunAsync(wt, ["src/A.cs", "src/B.cs"], CancellationToken.None);

        Assert.False(run.TimedOut);
        var m = Assert.Single(run.Mutants);
        Assert.Equal("src/A.cs", m.File);
        var cmd = sandbox.Commands[0];
        Assert.Contains("dotnet-stryker", cmd);
        Assert.Contains("--solution App.sln", cmd);
        Assert.Contains("--mutate '**/src/A.cs'", cmd);
        Assert.Contains("--mutate '**/src/B.cs'", cmd);
        Assert.Contains("--reporter json", cmd);
    }

    [Fact]
    public async Task A_timeout_is_reported_as_the_tool_not_finishing()
    {
        var sandbox = new FakeSandbox(_ => new ProcessResult(124, "", TimeSpan.Zero, true));
        var run = await new StrykerRunner(new Sandboxes(sandbox)).RunAsync(Worktree(), ["src/A.cs"], CancellationToken.None);
        Assert.True(run.TimedOut);
    }

    [Fact]
    public async Task No_report_means_the_tool_did_not_finish_which_says_nothing_about_the_tests()
    {
        var sandbox = new FakeSandbox(c => c.StartsWith("cat ") ? new ProcessResult(1, "no such file", TimeSpan.Zero, false) : new ProcessResult(1, "boom", TimeSpan.Zero, false));
        var run = await new StrykerRunner(new Sandboxes(sandbox)).RunAsync(Worktree(), ["src/A.cs"], CancellationToken.None);
        Assert.True(run.TimedOut);
    }

    [Fact]
    public async Task File_names_are_not_a_shell_injection_vector()
    {
        var sandbox = new FakeSandbox(_ => Ok());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new StrykerRunner(new Sandboxes(sandbox)).RunAsync(Worktree(), ["src/A.cs'; rm -rf /; '"], CancellationToken.None));
    }
}
