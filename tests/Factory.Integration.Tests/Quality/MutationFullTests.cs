using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Integration.Tests.Support;
using Factory.S2.Git;
using Factory.S2.Quality;
using Xunit;

namespace Factory.Integration.Tests.Quality;

public class MutationFullTests
{
    private const string Run = "r1";

    private static async Task<string> Git(string dir, params string[] args)
    {
        var r = await ProcessRunner.RunAsync("git", args, dir, TimeSpan.FromSeconds(30));
        Assert.True(r.Succeeded, r.Output);
        return r.Output.Trim();
    }

    /// A repo with a solution file and two commits; returns the repo path and the first commit (the "merged" one).
    private static async Task<(string Repo, string State, string Merged)> RepoAsync(bool solution = true)
    {
        var root = Directory.CreateTempSubdirectory("mutfull").FullName;
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "src"));
        if (solution) File.WriteAllText(Path.Combine(repo, "App.sln"), "");
        File.WriteAllText(Path.Combine(repo, "src/Calc.cs"), "// at merge\n");
        await Git(repo, "init", "-q", "-b", "main");
        await Git(repo, "config", "user.name", "t"); await Git(repo, "config", "user.email", "t@t");
        await Git(repo, "add", "-A"); await Git(repo, "commit", "-q", "-m", "merged");
        var merged = await Git(repo, "rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(repo, "src/Calc.cs"), "// later\n");
        await Git(repo, "commit", "-qam", "after");
        return (repo, Path.Combine(root, "state"), merged);
    }

    private static Mutant M(string id, string file, MutantStatus st) =>
        new(id, file, 1, "Arithmetic", st, Factory.Core.Core.ToList<string>([]), Factory.Core.Core.ToList<string>([]));

    private static MemoryLedger Merged(string? commit)
    {
        var ledger = new MemoryLedger();
        if (commit is not null)
            ledger.AppendAsync(new LedgerEvent(Run, "S2", "close", LedgerKinds.Merge, Outcome: "merged", Payload: JsonSerializer.Serialize(new { how = "pr x", commit }))).AsTask().Wait();
        return ledger;
    }

    [Fact]
    public async Task It_is_refused_until_the_run_has_a_recorded_merge()
    {
        var (repo, state, _) = await RepoAsync();
        var runner = new ScriptedMutationRunner();

        var result = await MutationFull.RunAsync(Merged(null), Run, [("", new GitWorkspace(repo, state))], runner, default);

        Assert.Contains("not merged", result.Refusal);
        Assert.Empty(runner.FullRuns);
    }

    [Fact]
    public async Task It_runs_the_whole_solution_on_a_detached_checkout_of_the_merged_commit_and_logs_the_score()
    {
        var (repo, state, merged) = await RepoAsync();
        var seen = "";
        var runner = new ScriptedMutationRunner { OnFullRun = wt => seen = File.ReadAllText(Path.Combine(wt, "src/Calc.cs")) };
        runner.Then(M("1", "src/Calc.cs", MutantStatus.Killed), M("2", "src/Calc.cs", MutantStatus.Survived), M("3", "src/Other.cs", MutantStatus.Killed));
        var ledger = Merged(merged);

        var result = await MutationFull.RunAsync(ledger, Run, [("", new GitWorkspace(repo, state))], runner, default);

        Assert.Null(result.Refusal);
        Assert.Equal("// at merge\n", seen); // the merged commit, not the branch tip
        Assert.Empty(runner.Asked);          // never a per-file run
        var row = Assert.Single(ledger.Events, e => e.Outcome == "mutation_full");
        Assert.Equal((LedgerKinds.Verify, "close"), (row.Kind, row.Stage));
        var p = JsonDocument.Parse(row.Payload!).RootElement;
        Assert.Equal(merged, p.GetProperty("commit").GetString());
        Assert.Equal(2.0 / 3.0, p.GetProperty("score").GetDouble(), 3);
        Assert.Equal(["src/Calc.cs", "src/Other.cs"], p.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("file").GetString()));
        Assert.Equal(0.5, p.GetProperty("files")[0].GetProperty("score").GetDouble(), 3);
        Assert.False(Directory.Exists(runner.FullRuns.Single()), "the checkout is removed");
    }

    [Fact]
    public async Task A_run_that_does_not_finish_logs_a_timed_out_row_and_a_close_is_unchanged()
    {
        var (repo, state, merged) = await RepoAsync();
        var runner = new ScriptedMutationRunner().ThenTimeout();
        var ledger = Merged(merged);

        await MutationFull.RunAsync(ledger, Run, [("", new GitWorkspace(repo, state))], runner, default);

        Assert.Contains(ledger.Events, e => e is { Outcome: "mutation_full_timed_out", Stage: "close" });
        Assert.Single(ledger.Events, e => e.Kind == LedgerKinds.Merge); // nothing reopened or rewritten
    }

    [Fact]
    public async Task A_split_run_is_mutated_once_per_repo_at_its_own_commit_and_a_repo_without_a_solution_is_skipped()
    {
        var (api, apiState, apiMerged) = await RepoAsync();
        var (ui, uiState, uiMerged) = await RepoAsync(solution: false);
        var runner = new ScriptedMutationRunner().Then(M("1", "src/Calc.cs", MutantStatus.Killed));
        var ledger = Merged($"Api@{apiMerged} Ui@{uiMerged}");

        var result = await MutationFull.RunAsync(ledger, Run, [("Api", new GitWorkspace(api, apiState, "Api")), ("Ui", new GitWorkspace(ui, uiState, "Ui"))], runner, default);

        Assert.Single(runner.FullRuns);
        var row = Assert.Single(ledger.Events, e => e.Outcome == "mutation_full");
        Assert.Equal("Api", JsonDocument.Parse(row.Payload!).RootElement.GetProperty("repo").GetString());
        Assert.Contains(result.Lines, l => l.Contains("Ui") && l.Contains("no solution"));
    }
}
