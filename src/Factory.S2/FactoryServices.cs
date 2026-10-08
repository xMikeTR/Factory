using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Harness.Policy;
using Factory.Harness.Sandbox;
using Factory.S2.Git;
using Factory.S2.Teams;
using Factory.S2.Verify;

namespace Factory.S2;

using Core = Factory.Core.Core; // "Core" alone binds to the namespace Factory.Core from inside Factory.*

public sealed record FactorySettings(
    string RepoPath,
    string RegistryPath,
    string BaseBranch,
    string StateDir,
    int MaxPlannerAttempts,
    Limits TaskLimits,
    IReadOnlyList<string> CommandAllowList,
    /// Full suite. Also the P2P baseline/regression check when it writes JUnit XML to
    /// $FACTORY_TEST_RESULTS (relative to the worktree); with no results the check is off.
    string IntegrationTestCommand,
    string? PObserveCommand,
    /// From the target repo's .factory/policy.yaml; null is the default (auto).
    MergePolicy? MergePolicy = null,
    /// From the same file's `quality:` section; null is off (no measuring).
    QualityPolicy? QualityPolicy = null)
{
    public static FactorySettings FromEnvironment(string repo, string? registry, string? baseBranch) => new(
        RepoPath: Path.GetFullPath(repo),
        RegistryPath: Path.GetFullPath(registry ?? Env("FACTORY_REGISTRY", Path.Combine(repo, "..", "registry"))),
        BaseBranch: baseBranch ?? Env("FACTORY_BASE_BRANCH", "main"),
        StateDir: Path.GetFullPath(Env("FACTORY_STATE_DIR", Path.Combine(repo, ".factory"))),
        MaxPlannerAttempts: int.Parse(Env("FACTORY_MAX_PLANNER_ATTEMPTS", "3")),
        TaskLimits: new Limits(                                     // (MaxTurns, MaxVerifyAttempts)
            int.Parse(Env("FACTORY_MAX_TURNS", "8")),
            int.Parse(Env("FACTORY_MAX_VERIFY_ATTEMPTS", "3"))),
        CommandAllowList: Env("FACTORY_COMMANDS", "dotnet build;dotnet test;dotnet format")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        IntegrationTestCommand: Env("FACTORY_INTEGRATION_TEST_CMD", "dotnet test"),
        PObserveCommand: Environment.GetEnvironmentVariable("FACTORY_POBSERVE_CMD"),
        MergePolicy: MergePolicyFile.FromEnvironment(Path.GetFullPath(repo)),
        QualityPolicy: QualityPolicyFile.Load(Path.GetFullPath(repo)));

    public string RunDir(string runId) => Path.Combine(StateDir, "runs", runId);
    public string PlanPath(string runId) => Path.Combine(RunDir(runId), "plan.json");
    public string DispatchPath(string runId) => Path.Combine(RunDir(runId), "dispatch.json");
    public string HandoverCopyPath(string runId) => Path.Combine(RunDir(runId), "handover.json");
    /// Regeneration: the previous run's id, then (once the plan is accepted) which tasks re-run.
    public string SincePath(string runId) => Path.Combine(RunDir(runId), "since");
    public string RegenerationPath(string runId) => Path.Combine(RunDir(runId), "regeneration.json");

    private static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) ?? fallback;
}

/// Everything the executors share. Executors stay thin: they move messages and call these.
public sealed class FactoryServices(
    FactorySettings settings,
    ILedger ledger,
    FactoryAgents agents,
    IConfigurator configurator,
    GitWorkspace git,
    VerifyRunner verify,
    ISandboxFactory sandboxes,
    IPullRequests pullRequests,
    IReviewer reviewer)
{
    public FactorySettings Settings { get; } = settings;
    public ILedger Ledger { get; } = ledger;
    public FactoryAgents Agents { get; } = agents;
    public IConfigurator Configurator { get; } = configurator;
    public GitWorkspace Git { get; } = git;
    public VerifyRunner Verify { get; } = verify;
    /// Where agent commands run during a turn (verify uses its own); never the host environment.
    public ISandboxFactory Sandboxes { get; } = sandboxes;
    public IPullRequests PullRequests { get; } = pullRequests;
    /// Judges each task's green diff before it can pass; on the reviewer pool, apart from the implementer's.
    public IReviewer Reviewer { get; } = reviewer;

    /// Handover and plan are re-read and re-validated from disk at each step rather than carried
    /// in messages: messages stay small and serialisable for checkpoints, and a resumed run
    /// re-checks its inputs instead of trusting them.
    public Handover LoadHandover(string runId) => Core.ParseHandover(File.ReadAllText(Settings.HandoverCopyPath(runId)));

    public ValidatedPlan LoadValidatedPlan(string runId)
    {
        var validation = Core.ValidatePlan(LoadHandover(runId), File.ReadAllText(Settings.PlanPath(runId)));
        return validation.IsValid
            ? validation.Plan
            : throw new FactoryRejectedException("stored plan no longer validates", validation.Errors);
    }

    public DispatchPlan LoadDispatch(string runId) => Core.Dispatch(LoadValidatedPlan(runId));

    /// The task ids a regeneration run re-runs; null when every task runs (not a regeneration).
    public IReadOnlySet<string>? LoadRerun(string runId) =>
        File.Exists(Settings.RegenerationPath(runId))
            ? JsonSerializer.Deserialize<RegenerationFile>(File.ReadAllText(Settings.RegenerationPath(runId)))!.Rerun.ToHashSet()
            : null;

    public ValueTask LogAsync(string runId, string stage, string kind, string? outcome = null, string? payload = null,
        string? teamId = null, string? taskId = null, CancellationToken ct = default) =>
        Ledger.AppendAsync(new LedgerEvent(runId, "S2", stage, kind, TaskId: taskId, TeamId: teamId, Outcome: outcome, Payload: payload), ct);
}

/// regeneration.json: what changed since the previous run and which tasks re-run because of it.
public sealed record RegenerationFile(string Since, string[] Changed, string[] Rerun);
