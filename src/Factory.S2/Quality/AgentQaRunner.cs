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
    Func<string, IReadOnlyList<string>> requirements, IReadOnlyList<string> commandAllowList, TimeSpan? testTimeout = null,
    Func<CancellationToken, Task<QaEnvironment>>? environment = null) : IQaRunner
{
    public const string TestDir = "tests/qa";
    public const string ManifestFile = "manifest.json";
    private static readonly TimeSpan DefaultTestTimeout = TimeSpan.FromMinutes(10);

    public async Task<QaRun> RunAsync(string runId, string integrationPath, CancellationToken ct)
    {
        var reqs = requirements(runId);
        if (reqs.Count == 0) return new QaRun(reqs, [], false);

        // The sibling services (qa-env.yaml) live as long as the QA run; the author and every test run on their network.
        await using var env = environment is null ? null : await environment(ct);
        var boxes = env?.Sandboxes ?? sandboxes;
        var vars = env?.Env ?? new Dictionary<string, string>();

        var declared = await AuthorAsync(boxes, runId, integrationPath, reqs, ct);
        if (declared.Count == 0) return new QaRun(reqs, [], false);
        WriteManifest(integrationPath, new QaManifest(reqs, declared.Select(d => new ManifestTest(d.Id, d.Requirement, d.IsNew, d.Command)).ToList()));
        return await MeasureAsync(boxes, vars, runId, integrationPath, reqs, declared, ct);
    }

    /// The human follow-up: the author's tests are protected, so they are not written again. The manifest the first run left
    /// in tests/qa says what to run; the same measurement is taken on a fresh base-branch checkout and the integrated result.
    public async Task<QaRun> RerunAsync(string runId, string integrationPath, CancellationToken ct)
    {
        var manifestPath = Path.Combine(integrationPath, TestDir, ManifestFile);
        if (!File.Exists(manifestPath)) return new QaRun([], [], false);
        var manifest = System.Text.Json.JsonSerializer.Deserialize<QaManifest>(await File.ReadAllTextAsync(manifestPath, ct))!;
        await using var env = environment is null ? null : await environment(ct);
        return await MeasureAsync(env?.Sandboxes ?? sandboxes, env?.Env ?? new Dictionary<string, string>(), runId, integrationPath, manifest.Requirements,
            manifest.Tests.Select(t => new QaTestDecl(t.Id, t.Requirement, t.IsNew, t.Command)).ToList(), ct);
    }

    private async Task<QaRun> MeasureAsync(ISandboxFactory boxes, IReadOnlyDictionary<string, string> vars, string runId, string integrationPath,
        IReadOnlyList<string> reqs, IReadOnlyList<QaTestDecl> declared, CancellationToken ct)
    {
        await using var baseline = await git.BaselineCheckoutAsync(runId, ct, baseBranch);
        CopyDirectory(Path.Combine(integrationPath, TestDir), Path.Combine(baseline.Path, TestDir));

        var tests = new List<QaTest>();
        foreach (var t in declared)
        {
            var onBase = await RunOneAsync(boxes, vars, "qa-baseline", baseline.Path, t.Command, ct);
            var onIntegrated = await RunOneAsync(boxes, vars, "qa-integrated", integrationPath, t.Command, ct);
            if (onBase is null || onIntegrated is null) return new QaRun(reqs, [], true);
            tests.Add(new QaTest(t.Id, t.Requirement, t.IsNew ? QaKind.New : QaKind.Characterisation, onBase, onIntegrated));
        }
        return new QaRun(reqs, tests, false);
    }

    private sealed record ManifestTest(string Id, string Requirement, bool IsNew, string Command);
    private sealed record QaManifest(IReadOnlyList<string> Requirements, IReadOnlyList<ManifestTest> Tests);

    private static void WriteManifest(string integrationPath, QaManifest manifest)
    {
        Directory.CreateDirectory(Path.Combine(integrationPath, TestDir));
        File.WriteAllText(Path.Combine(integrationPath, TestDir, ManifestFile), System.Text.Json.JsonSerializer.Serialize(manifest));
    }

    private async Task<IReadOnlyList<QaTestDecl>> AuthorAsync(ISandboxFactory sandboxes, string runId, string integrationPath, IReadOnlyList<string> reqs, CancellationToken ct)
    {
        // QA reads the system before it writes: every result stays in view (trimming made it re-read the same files until the cap).
        var signals = new TurnSignals();
        await using var sandbox = await sandboxes.ForTeamAsync("qa", integrationPath, ct);
        var workspace = new ScopedWorkspace(integrationPath, [PathPatternModule.createOrFail(TestDir + "/**")], [], commandAllowList, signals, sandbox);
        var agent = agents.Create(new AgentScope(runId, Roles.Qa, "qa", "qa"), ModelTier.Standard,
            Briefs.Prompt("qa-author"), workspace.QaAuthorTools(), new TurnBudget(30, 24), keepRecentResults: int.MaxValue);
        await agent.RunAsync("## Requirements this run was meant to deliver\n" + string.Join("\n", reqs.Select(r => "- " + r)), cancellationToken: ct);
        return signals.QaTests ?? [];
    }

    /// Passed, failed, or null when the command timed out.
    private async Task<QaOutcome?> RunOneAsync(ISandboxFactory sandboxes, IReadOnlyDictionary<string, string> env, string team, string worktree, string command, CancellationToken ct)
    {
        await using var sandbox = await sandboxes.ForTeamAsync(team, worktree, ct);
        var r = await sandbox.RunAsync(command, worktree, testTimeout ?? DefaultTestTimeout, env, ct);
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
