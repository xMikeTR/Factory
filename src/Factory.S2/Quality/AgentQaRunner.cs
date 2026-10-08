using Factory.Core;
using Factory.Harness;
using Factory.Harness.Sandbox;
using Factory.S2.Git;
using Factory.S2.Teams;
using Core = Factory.Core.Core;

namespace Factory.S2.Quality;

/// QA by an agent of the reviewer pool: it writes black-box tests under tests/qa in the integrated checkout, then each test's
/// command runs on a checkout of the base branch (with the same tests copied in) and on the integrated result. The judgement
/// is Core's `Qa.judge`; this only measures. Any run that times out makes the whole QA a timeout: never evidence.
public sealed class AgentQaRunner(
    FactoryAgents agents, GitWorkspace git, ISandboxFactory sandboxes, string baseBranch,
    Func<string, IReadOnlyList<string>> requirements, IReadOnlyList<string> commandAllowList, TimeSpan? testTimeout = null) : IQaRunner
{
    public const string TestDir = "tests/qa";
    private static readonly TimeSpan DefaultTestTimeout = TimeSpan.FromMinutes(10);

    public async Task<QaRun> RunAsync(string runId, string integrationPath, CancellationToken ct)
    {
        var reqs = requirements(runId);
        if (reqs.Count == 0) return new QaRun(reqs, [], false);

        var declared = await AuthorAsync(runId, integrationPath, reqs, ct);
        if (declared.Count == 0) return new QaRun(reqs, [], false);

        await using var baseline = await git.BaselineCheckoutAsync(runId, ct, baseBranch);
        CopyDirectory(Path.Combine(integrationPath, TestDir), Path.Combine(baseline.Path, TestDir));

        var tests = new List<QaTest>();
        foreach (var t in declared)
        {
            var onBase = await RunOneAsync("qa-baseline", baseline.Path, t.Command, ct);
            var onIntegrated = await RunOneAsync("qa-integrated", integrationPath, t.Command, ct);
            if (onBase is null || onIntegrated is null) return new QaRun(reqs, [], true);
            tests.Add(new QaTest(t.Id, t.Requirement, t.IsNew ? QaKind.New : QaKind.Characterisation, onBase, onIntegrated));
        }
        return new QaRun(reqs, tests, false);
    }

    private async Task<IReadOnlyList<QaTestDecl>> AuthorAsync(string runId, string integrationPath, IReadOnlyList<string> reqs, CancellationToken ct)
    {
        var signals = new TurnSignals();
        await using var sandbox = await sandboxes.ForTeamAsync("qa", integrationPath, ct);
        var workspace = new ScopedWorkspace(integrationPath, [PathPatternModule.createOrFail(TestDir + "/**")], [], commandAllowList, signals, sandbox);
        var agent = agents.Create(new AgentScope(runId, Roles.Qa, "qa", "qa"), ModelTier.Standard,
            Briefs.Prompt("qa-author"), workspace.QaAuthorTools(), new TurnBudget());
        await agent.RunAsync("## Requirements this run was meant to deliver\n" + string.Join("\n", reqs.Select(r => "- " + r)), cancellationToken: ct);
        return signals.QaTests ?? [];
    }

    /// Passed, failed, or null when the command timed out.
    private async Task<QaOutcome?> RunOneAsync(string team, string worktree, string command, CancellationToken ct)
    {
        await using var sandbox = await sandboxes.ForTeamAsync(team, worktree, ct);
        var r = await sandbox.RunAsync(command, worktree, testTimeout ?? DefaultTestTimeout, new Dictionary<string, string>(), ct);
        return r.TimedOut ? null : r.ExitCode == 0 ? QaOutcome.QaPassed : QaOutcome.QaFailed;
    }

    private static void CopyDirectory(string from, string to)
    {
        if (!Directory.Exists(from)) return;
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
