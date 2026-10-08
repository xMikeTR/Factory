using Factory.Core;
using Factory.S2.Quality;
using Xunit;

namespace Factory.Integration.Tests.Quality;

public class StrykerReportTests
{
    private const string Json = """
        { "schemaVersion": "1", "files": {
            "/work/repo/src/Rater.cs": { "language": "cs", "mutants": [
              { "id": "1", "mutatorName": "Arithmetic", "status": "Killed",  "location": { "start": { "line": 12, "column": 5 }, "end": { "line": 12, "column": 9 } } },
              { "id": "2", "mutatorName": "Equality",   "status": "Survived","location": { "start": { "line": 14, "column": 5 }, "end": { "line": 14, "column": 9 } } },
              { "id": "3", "mutatorName": "Block",      "status": "NoCoverage","location": { "start": { "line": 20, "column": 1 }, "end": { "line": 22, "column": 1 } } },
              { "id": "4", "mutatorName": "String",     "status": "CompileError","location": { "start": { "line": 3, "column": 1 }, "end": { "line": 3, "column": 2 } } },
              { "id": "5", "mutatorName": "Linq",       "status": "RuntimeError","location": { "start": { "line": 4, "column": 1 }, "end": { "line": 4, "column": 2 } } },
              { "id": "6", "mutatorName": "Linq",       "status": "Timeout", "location": { "start": { "line": 5, "column": 1 }, "end": { "line": 5, "column": 2 } } } ] } } }
        """;

    [Fact]
    public void Mutants_are_read_with_repo_relative_files_lines_and_statuses()
    {
        var mutants = StrykerReport.Parse(Json, "/work/repo");

        Assert.Equal(6, mutants.Count);
        var survivor = mutants.Single(m => m.Id == "2");
        Assert.Equal("src/Rater.cs", survivor.File);
        Assert.Equal(14, survivor.Line);
        Assert.Equal("Equality", survivor.Mutator);
        Assert.Equal(MutantStatus.Survived, survivor.Status);
        Assert.Equal(MutantStatus.NoCoverage, mutants.Single(m => m.Id == "3").Status);
        Assert.Equal(MutantStatus.Timeout, mutants.Single(m => m.Id == "6").Status);
    }

    [Fact]
    public void A_mutant_that_crashed_the_test_run_says_nothing_about_the_tests()
    {
        var mutants = StrykerReport.Parse(Json, "/work/repo");

        Assert.Equal(MutantStatus.Ignored, mutants.Single(m => m.Id == "5").Status);
    }
}
