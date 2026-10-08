using Factory.Core;

namespace Factory.S2.Quality;

/// What one QA run found: the requirements the author derived, and each black-box test's outcome on the
/// baseline and on the integrated result. TimedOut means the environment or tests never finished.
public sealed record QaRun(IReadOnlyList<string> Requirements, IReadOnlyList<QaTest> Tests, bool TimedOut);

/// Writes and runs requirement-level black-box tests after the integration checks pass. Behind a seam because
/// the author is an agent on the reviewer pool and the environment is sibling containers from qa-env.yaml.
public interface IQaRunner
{
    Task<QaRun> RunAsync(string runId, string integrationPath, CancellationToken ct);
}

public static class QaGate
{
    /// The brief that ends the run: what QA found, and what Designc must decide. Escapes (regressions the task
    /// gates let through) are named apart, because they say the factory's own gates missed something.
    public static string Brief(IEnumerable<QaFinding> findings)
    {
        var lines = findings.Select(f => f switch
        {
            QaFinding.Uncovered u => $"- requirement {u.requirement} has no passing new test",
            QaFinding.Vacuous v => $"- {v.test}: a new test that already passes on the baseline proves nothing about this run",
            QaFinding.NotDone n => $"- {n.test}: still fails on the integrated result (requirement not met)",
            QaFinding.Escape e => $"- ESCAPE {e.test}: passed before this run and fails now; the task gates let a regression through",
            QaFinding.InvalidCharacterisation i => $"- {i.test}: a characterisation test that fails on the baseline does not describe existing behaviour",
            _ => throw new InvalidOperationException($"unknown QA finding {f}"),
        }).ToList();
        return "QA found problems with the integrated result, so nothing merges:\n" + string.Join("\n", lines);
    }
}
