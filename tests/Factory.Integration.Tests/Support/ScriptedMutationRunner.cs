using Factory.Core;
using Factory.S2.Quality;

namespace Factory.Integration.Tests.Support;

/// A mutation tool that answers from a queue and finds nothing once it is empty. Records the files it was asked about.
public sealed class ScriptedMutationRunner : IMutationRunner
{
    private readonly Queue<MutationRun> _runs = new();
    public List<IReadOnlyList<string>> Asked { get; } = [];

    public ScriptedMutationRunner Then(params Mutant[] mutants) { _runs.Enqueue(new MutationRun(mutants, false)); return this; }
    public ScriptedMutationRunner ThenTimeout() { _runs.Enqueue(new MutationRun([], true)); return this; }

    public Task<MutationRun> RunAsync(string worktree, IReadOnlyList<string> files, CancellationToken ct)
    {
        Asked.Add(files);
        return Task.FromResult(_runs.Count > 0 ? _runs.Dequeue() : new MutationRun([], false));
    }
}
