using Factory.Core;

namespace Factory.S2.Quality;

/// What one mutation run found. TimedOut means the tool did not finish, which says nothing about the tests.
/// Skipped is the reason when a `--mutate` glob matched no file (Stryker would mutate the whole project then): those globs were not run.
public sealed record MutationRun(IReadOnlyList<Mutant> Mutants, bool TimedOut, string? Skipped = null);

/// Runs a mutation tool over the given repo-relative files of a worktree. Behind a seam because the tool
/// (Stryker.NET for C#) is installed in the runner image, not on the host; tests use a scripted runner.
public interface IMutationRunner
{
    Task<MutationRun> RunAsync(string worktree, IReadOnlyList<string> files, CancellationToken ct);

    /// The whole solution, no `--mutate`: the full run after a merge (long, so its own timeout).
    Task<MutationRun> RunAllAsync(string worktree, CancellationToken ct);
}
