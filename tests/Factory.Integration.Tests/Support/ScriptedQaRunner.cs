using Factory.Core;
using Factory.S2.Quality;

namespace Factory.Integration.Tests.Support;

/// A QA runner that returns what the test gave it. Records the integration path it was pointed at.
public sealed class ScriptedQaRunner(QaRun run) : IQaRunner
{
    public List<string> Asked { get; } = [];

    public Task<QaRun> RunAsync(string runId, string integrationPath, CancellationToken ct)
    {
        Asked.Add(integrationPath);
        return Task.FromResult(run);
    }
}
