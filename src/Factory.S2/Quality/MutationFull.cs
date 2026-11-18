using System.Text.Json;
using Factory.Core;
using Microsoft.FSharp.Collections;
using Factory.Harness;
using Factory.S2.Git;

namespace Factory.S2.Quality;

/// `factory-s2 mutation-full`: the whole-solution mutation run on the merged commit, after `close` has recorded the merge.
/// Report only: it writes `verify` rows (stage close) and never touches the close. Too long to run inside `close`.
public static class MutationFull
{
    /// A line per repo for the person, and why nothing ran (null when something was attempted).
    public sealed record Result(string? Refusal, IReadOnlyList<string> Lines);

    public static TimeSpan Timeout(string? minutes) =>
        double.TryParse(minutes, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m) && m > 0
            ? TimeSpan.FromMinutes(m) : TimeSpan.FromMinutes(120);

    /// `repos` is (name, git): one entry with an empty name for a single-repo run. The merge row's commit is a sha, or
    /// "Api@sha Ui@sha" for a split run.
    public static async Task<Result> RunAsync(ILedger ledger, string runId, IReadOnlyList<(string Repo, GitWorkspace Git)> repos,
        IMutationRunner runner, CancellationToken ct)
    {
        string? commit = null;
        await foreach (var e in ledger.ReadAsync(runId, ct))
            if (e is { Kind: LedgerKinds.Merge, Stage: "close", Outcome: "merged" } && e.Payload is { } p
                && JsonDocument.Parse(p).RootElement.TryGetProperty("commit", out var c)) commit = c.GetString();
        if (commit is null) return new Result($"run {runId} is not merged: record the merge with `close` first", []);

        var commits = commit.Contains('@')
            ? commit.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Split('@', 2)).ToDictionary(t => t[0], t => t[1])
            : new Dictionary<string, string> { [""] = commit };
        var lines = new List<string>();
        foreach (var (repo, git) in repos)
        {
            var label = repo.Length == 0 ? "repo" : repo;
            if (!commits.TryGetValue(repo, out var sha)) { lines.Add($"{label}: the merge row has no commit for it"); continue; }
            await using var checkout = await git.BaselineCheckoutAsync(runId, ct, sha);
            if (!Directory.EnumerateFiles(checkout.Path, "*.sln*", SearchOption.TopDirectoryOnly).Any(f => f.EndsWith(".sln") || f.EndsWith(".slnx")))
            { lines.Add($"{label}: no solution at its root, skipped (Stryker.NET mutates C# solutions)"); continue; }

            var run = await runner.RunAllAsync(checkout.Path, ct);
            if (run.TimedOut)
            {
                await ledger.AppendAsync(new LedgerEvent(runId, "S2", "close", LedgerKinds.Verify, Outcome: "mutation_full_timed_out",
                    Payload: Serialize(repo, sha, null, [])), ct);
                lines.Add($"{label}: the mutation run did not finish in time");
                continue;
            }
            var all = run.Mutants.Select(m => m.File).Distinct().Select(f => new ChangedRange(f, 1, int.MaxValue)).ToList();
            var summary = Mutation.summary(new MutationThresholds(0), MapModule.Empty<string, double>(), SetModule.Empty<string>(),
                Factory.Core.Core.ToList(all), Factory.Core.Core.ToList(run.Mutants));
            var score = summary.Score is { } s ? s.Value : (double?)null;
            await ledger.AppendAsync(new LedgerEvent(runId, "S2", "close", LedgerKinds.Verify, Outcome: "mutation_full",
                Payload: Serialize(repo, sha, score, summary.Files.Select(f => (f.File, f.Score is { } v ? v.Value : (double?)null)).ToList())), ct);
            lines.Add($"{label}: {run.Mutants.Count} mutants, whole-repo score {(score is { } x ? x.ToString("P1") : "n/a")}");
        }
        return new Result(null, lines);
    }

    private static string Serialize(string repo, string commit, double? score, IReadOnlyList<(string File, double? Score)> files)
    {
        var rows = files.Select(f => new { file = f.File, score = f.Score });
        return repo.Length == 0
            ? JsonSerializer.Serialize(new { commit, score, files = rows })
            : JsonSerializer.Serialize(new { repo, commit, score, files = rows });
    }
}
