using Factory.Core;

namespace Factory.S2.Quality;

/// What one mutation run found. TimedOut means the tool did not finish, which says nothing about the tests.
public sealed record MutationRun(IReadOnlyList<Mutant> Mutants, bool TimedOut);

/// Runs a mutation tool over the given repo-relative files of a worktree. Behind a seam because the tool
/// (Stryker.NET for C#) is installed in the runner image, not on the host; tests use a scripted runner.
public interface IMutationRunner
{
    Task<MutationRun> RunAsync(string worktree, IReadOnlyList<string> files, CancellationToken ct);
}
