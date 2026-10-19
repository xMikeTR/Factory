using Factory.Core;
using Factory.Harness;
using Factory.Harness.Governance;
using Factory.Harness.Policy;
using Factory.Harness.Sandbox;
using Factory.Harness.Telemetry;
using Factory.S2;
using Factory.S2.Git;
using Factory.S2.Quality;
using Factory.S2.Teams;
using Factory.S2.Verify;

namespace Factory.S2;

using Core = Factory.Core.Core;

/// Everything a run (or a rerun) needs, wired from the environment.
public static class ServiceWiring
{
    /// S2's own ledger, projecting each attempt start, task state change and end onto the registry as it happens.
    public static ILedger ProgressLedger(FactorySettings settings) => new Factory.Harness.Registry.RegistryProgressLedger(
        new JsonlLedger(Path.Combine(settings.StateDir, "ledger.jsonl")), settings.RegistryPath,
        id => Factory.Harness.Registry.HandoverRef.From(Factory.Core.Core.ParseHandover(File.ReadAllText(settings.HandoverCopyPath(id)))));

    public static FactoryServices Services(FactorySettings settings, string runId, Dictionary<string, string> env, IPullRequests pullRequests)
    {
        var ledger = ProgressLedger(settings);
        // FACTORY_SANDBOX is an ordered list (docker,local); the first that starts is used. Default docker only;
        // local is the explicit, unisolated fallback and runs only when named.
        ISandboxFactory SandboxNamed(string name) => name switch
        {
            "docker" => new DockerSandboxFactory(new DockerSandboxOptions(
                Image: Environment.GetEnvironmentVariable("FACTORY_RUNNER_IMAGE") ?? "factory-s2-runner:latest", NpmRebuild: settings.Npm?.Rebuild), ledger, runId),
            _ => new LocalSandboxFactory(),
        };
        ISandboxFactory inner = new FallbackSandboxFactory(
            FallbackSandboxFactory.Parse(Environment.GetEnvironmentVariable("FACTORY_SANDBOX")).Select(n => (n, SandboxNamed(n))).ToList(),
            (name, why) => ledger.AppendAsync(new LedgerEvent(runId, "S2", "sandbox_skipped", LedgerKinds.Verify, Outcome: name,
                Payload: System.Text.Json.JsonSerializer.Serialize(new { sandbox = name, reason = why })), default).AsTask());
        // Heavy commands share the machine's CPU across teams; a timeout is re-run once alone (FACTORY_HEAVY_SLOTS).
        var flaky = settings.QualityPolicy?.Flaky ?? [];
        ISandboxFactory sandboxes = new GatedSandboxFactory(inner, new HeavyGate(
            int.TryParse(Environment.GetEnvironmentVariable("FACTORY_HEAVY_SLOTS"), out var slots) && slots > 0 ? slots : HeavyGate.DefaultSlots),
            isFlaky: command => flaky.Any(command.Contains),
            onFlakyRetry: command => ledger.AppendAsync(new LedgerEvent(runId, "S2", "flaky_retry", LedgerKinds.Verify, Outcome: "re-run alone", Payload: System.Text.Json.JsonSerializer.Serialize(new { command })), default).AsTask());
        // Tool governance: the AGT policy files per role (policies/s2) by default; the in-code
        // allow-list is the explicit fallback and the contract-test double.
        IToolPolicy toolPolicy = (Environment.GetEnvironmentVariable("FACTORY_TOOL_POLICY") ?? "acs") switch
        {
            "acs" => AcsToolPolicy.Load(Environment.GetEnvironmentVariable("FACTORY_POLICY_DIR") ?? AcsToolPolicy.DefaultDirectory),
            "allowlist" => new RoleAllowListPolicy(),
            var other => throw new ArgumentException($"FACTORY_TOOL_POLICY: '{other}' is not acs|allowlist"),
        };
        // Laya routes turns when FACTORY_LAYA_URL points at tools/laya-sidecar and it is confident
        // enough; otherwise (and always when unset) the heuristic decides. The ledger names who did.
        IConfigurator configurator = Environment.GetEnvironmentVariable("FACTORY_LAYA_URL") is { Length: > 0 } laya
            ? new FallbackConfigurator(
                new LayaConfigurator(new HttpClient { BaseAddress = new Uri(laya), Timeout = TimeSpan.FromSeconds(5) }),
                new HeuristicConfigurator(),
                double.Parse(Environment.GetEnvironmentVariable("FACTORY_LAYA_MIN_CONFIDENCE") ?? "0.7", System.Globalization.CultureInfo.InvariantCulture))
            : new HeuristicConfigurator();
        var git = new GitWorkspace(settings.RepoPath, settings.StateDir);
        var gateways = ModelGateways.FromEnvironment(env, mergesAreDryRun: pullRequests is DryRunPullRequests);
        var agents = new FactoryAgents(gateways, toolPolicy, ledger, FactoryTelemetry.CaptureContent(env));
        // QA runs only for a repo that describes a QA environment (.factory/qa-env.yaml); its requirements are the plan's tasks
        // (default source, until the handover carries requirements of its own).
        FactoryServices? built = null;
        var qaEnv = QaEnvFile.Load(settings.RepoPath);
        var dockerOptions = new DockerSandboxOptions(
            Image: Environment.GetEnvironmentVariable("FACTORY_RUNNER_IMAGE") ?? "factory-s2-runner:latest", NpmRebuild: settings.Npm?.Rebuild);
        // The qa-env.yaml services run on their own docker network for the length of one QA run; the QA sandboxes join it (docker only).
        IQaRunner? qa = qaEnv is null ? null
            : new AgentQaRunner(agents, git, sandboxes, settings.BaseBranch,
                id => built!.LoadValidatedPlan(id).Plan.Tasks.Select(t => $"{t.Id.Value}: {t.Title}").ToList(), settings.CommandAllowList,
                environment: inner is DockerSandboxFactory ? ct => QaEnvironment.StartAsync(qaEnv, dockerOptions, ct) : null);
        built = new FactoryServices(
            settings,
            ledger,
            agents,
            configurator,
            git,
            new VerifyRunner(ledger, sandboxes, git),
            sandboxes,
            pullRequests,
            new AgentReviewer(agents, gateways.ReviewerIndependence, settings.CommandAllowList),
            mutation: new StrykerRunner(sandboxes),
            qa: qa);
        return built;
    }
}
