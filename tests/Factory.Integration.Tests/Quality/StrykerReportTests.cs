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

    private const string WithTests = """
        { "schemaVersion": "1",
          "testFiles": { "/work/repo/tests/RaterTests.cs": { "language": "cs", "tests": [
              { "id": "t-1", "name": "RaterTests.Adds" }, { "id": "t-2", "name": "RaterTests.Rounds" } ] } },
          "files": { "/work/repo/src/Rater.cs": { "language": "cs", "mutants": [
              { "id": "1", "mutatorName": "Arithmetic", "status": "Killed",   "coveredBy": ["t-1", "t-2"], "killedBy": ["t-1"], "location": { "start": { "line": 12, "column": 5 } } },
              { "id": "2", "mutatorName": "Equality",   "status": "Survived", "coveredBy": ["t-2", "t-9"], "location": { "start": { "line": 14, "column": 5 } } },
              { "id": "3", "mutatorName": "Block",      "status": "NoCoverage", "coveredBy": [], "killedBy": [], "location": { "start": { "line": 20, "column": 1 } } },
              { "id": "4", "mutatorName": "Linq",       "status": "Survived", "location": { "start": { "line": 21, "column": 1 } } } ] } } }
        """;

    [Fact]
    public void Test_ids_are_resolved_to_names_through_the_test_files()
    {
        var mutants = StrykerReport.Parse(WithTests, "/work/repo");

        var killed = mutants.Single(m => m.Id == "1");
        Assert.Equal(["RaterTests.Adds", "RaterTests.Rounds"], killed.CoveredBy);
        Assert.Equal(["RaterTests.Adds"], killed.KilledBy);
        Assert.Equal(["RaterTests.Rounds", "t-9"], mutants.Single(m => m.Id == "2").CoveredBy); // an id the report does not list stays as the id
        Assert.Empty(mutants.Single(m => m.Id == "2").KilledBy);
    }

    [Fact]
    public void A_report_without_coveredBy_gives_empty_lists()
    {
        var mutants = StrykerReport.Parse(Json, "/work/repo");

        Assert.All(mutants, m => { Assert.Empty(m.CoveredBy); Assert.Empty(m.KilledBy); });
    }

    [Fact]
    public void A_survivor_line_names_the_tests_that_ran_it_or_says_why_it_cannot()
    {
        var m = StrykerReport.Parse(WithTests, "/work/repo");

        Assert.Equal("tests that ran it: RaterTests.Rounds, t-9", Mutation.testsThatRan(m.Single(x => x.Id == "2")));
        Assert.Equal("no test reaches this line", Mutation.testsThatRan(m.Single(x => x.Id == "3")));
        Assert.Equal("the tool did not say which tests ran it", Mutation.testsThatRan(m.Single(x => x.Id == "4")));
    }

    [Fact]
    public void The_real_stryker_4_16_report_parses_with_test_names()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "stryker", "report-4.16.json");
        var json = File.ReadAllText(path);
        var root = System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("projectRoot").GetString()!;

        var mutants = StrykerReport.Parse(json, root);

        var killed = mutants.Single(m => m.Status.IsKilled);
        Assert.Equal(["CalcTests.Adds"], killed.CoveredBy);
        Assert.Equal(["CalcTests.Adds"], killed.KilledBy);
        var survivor = mutants.Single(m => m.Status.IsSurvived);
        Assert.Equal(["CalcTests.TwiceRuns"], survivor.CoveredBy);
        Assert.Empty(survivor.KilledBy);
        Assert.All(mutants.Where(m => m.Status.IsNoCoverage), m => Assert.Empty(m.CoveredBy));
    }
}
