using Factory.Core;
using Factory.Harness;
using Factory.Harness.Sandbox;
using Factory.S2.Quality;
using Xunit;

namespace Factory.Integration.Tests.Quality;

public class TsImportEdgesTests
{
    private const string Report = """
        {"modules":[
          {"source":"web/src/domain/rate.ts","dependencies":[
            {"resolved":"web/src/infra/db.ts","coreModule":false,"couldNotResolve":false,"dependencyTypes":["local"]},
            {"resolved":"node_modules/react/index.js","coreModule":false,"couldNotResolve":false,"dependencyTypes":["npm"]},
            {"resolved":"fs","coreModule":true,"couldNotResolve":false,"dependencyTypes":["core"]},
            {"resolved":"./missing","coreModule":false,"couldNotResolve":true,"dependencyTypes":["unknown"]}]},
          {"source":"web/src/infra/db.ts","dependencies":[]}],
         "summary":{"error":0}}
        """;

    private sealed class FakeSandbox(string output) : ICommandSandbox
    {
        public List<string> Commands { get; } = [];
        public string Kind => "fake";
        public Task<ProcessResult> RunAsync(string command, string workdir, TimeSpan timeout, IReadOnlyDictionary<string, string> env, CancellationToken ct)
        {
            Commands.Add(command);
            return Task.FromResult(new ProcessResult(0, output, TimeSpan.Zero, false));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void Only_imports_between_files_of_the_repo_are_edges()
    {
        var edge = Assert.Single(TsImportEdges.Parse(Report));

        Assert.Equal(("web/src/domain/rate.ts", "web/src/infra/db.ts"), (edge.Source, edge.Target));
    }

    [Fact]
    public async Task The_report_is_read_through_noise_and_garbage_reads_as_no_edges()
    {
        var sandbox = new FakeSandbox("npm warn x\n" + Report.Replace("{\"modules\":[", "{\n  \"modules\": [")); // depcruise pretty-prints

        Assert.Single(await TsImportEdges.ReadAsync(sandbox, "/w", CancellationToken.None));
        Assert.Contains("depcruise", sandbox.Commands[0]);
        Assert.Empty(await TsImportEdges.ReadAsync(new FakeSandbox("depcruise: command not found"), "/w", CancellationToken.None));
    }

    [Fact]
    public void An_import_the_baseline_already_had_is_debt_and_a_new_one_is_a_violation()
    {
        var rule = new ForbiddenDependency(PathPatternModule.createOrFail("web/src/domain/**"), PathPatternModule.createOrFail("web/src/infra/**"), "domain is pure");
        var old = new CodeEdge("web/src/domain/old.ts", "web/src/infra/db.ts");
        var added = new CodeEdge("web/src/domain/rate.ts", "web/src/infra/db.ts");

        Assert.Empty(TsGate.Judge([rule], [old], [old]));
        var v = Assert.Single(TsGate.Judge([rule], [old], [old, added]));
        Assert.Equal(added, v.Edge);
        Assert.Empty(TsGate.Judge([], [], [added]));
    }
}
