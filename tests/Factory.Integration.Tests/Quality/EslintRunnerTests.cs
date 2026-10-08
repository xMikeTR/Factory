using Factory.Harness;
using Factory.Harness.Sandbox;
using Factory.S2.Quality;
using Xunit;

namespace Factory.Integration.Tests.Quality;

public class EslintRunnerTests
{
    private sealed class FakeSandbox(string output, int exit = 1) : ICommandSandbox
    {
        public List<string> Commands { get; } = [];
        public string Kind => "fake";
        public Task<ProcessResult> RunAsync(string command, string workdir, TimeSpan timeout, IReadOnlyDictionary<string, string> env, CancellationToken ct)
        {
            Commands.Add(command);
            return Task.FromResult(new ProcessResult(exit, output, TimeSpan.Zero, false));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string Json(string root) =>
        "npm warn something\n[{\"filePath\":\"" + root + "/web/a.ts\",\"messages\":[{\"ruleId\":\"complexity\",\"severity\":1,"
        + "\"message\":\"Function 'f' has a complexity of 4. Maximum allowed is 0.\",\"line\":2,\"endLine\":9}]}]";

    [Fact]
    public async Task Measures_the_file_on_disk_with_complexity_at_zero_and_ignores_noise_before_the_json()
    {
        var sandbox = new FakeSandbox(Json("/w"));
        var fns = await EslintRunner.MeasureAsync(sandbox, "/w", "web/a.ts", null, CancellationToken.None);
        var f = Assert.Single(fns);
        Assert.Equal(("web/a.ts", "f", 4), (f.File, f.Name, f.Cyclomatic));
        Assert.Contains("eslint", sandbox.Commands[0]);
        Assert.Contains("complexity", sandbox.Commands[0]);
        Assert.Contains("'web/a.ts'", sandbox.Commands[0]);
        Assert.DoesNotContain("--stdin", sandbox.Commands[0]);
    }

    [Fact]
    public async Task Measures_earlier_source_over_stdin_under_the_same_file_name_so_the_repos_config_applies()
    {
        var sandbox = new FakeSandbox(Json("/w"));
        await EslintRunner.MeasureAsync(sandbox, "/w", "web/a.ts", "export function f() {}\n", CancellationToken.None);
        Assert.Contains("--stdin --stdin-filename 'web/a.ts'", sandbox.Commands[0]);
        Assert.Contains("export function f() {}", sandbox.Commands[0]);
    }

    [Fact]
    public async Task A_run_that_prints_no_report_measures_nothing()
    {
        Assert.Empty(await EslintRunner.MeasureAsync(new FakeSandbox("eslint: command not found", 127), "/w", "web/a.ts", null, CancellationToken.None));
    }

    [Fact]
    public async Task An_unsafe_file_name_is_not_measured()
    {
        var sandbox = new FakeSandbox("[]");
        Assert.Empty(await EslintRunner.MeasureAsync(sandbox, "/w", "web/a'; rm -rf /.ts", null, CancellationToken.None));
        Assert.Empty(sandbox.Commands);
    }
}
