using Factory.Core;
using Factory.Harness;
using Factory.Harness.Governance;
using Factory.Harness.Sandbox;
using Factory.Harness.Telemetry;
using Factory.S2;
using Factory.S2.Git;
using Factory.S2.Quality;
using Factory.S2.Teams;
using Factory.S2.Verify;

// factory-s2 validate --handover h.json --plan plan.json      deterministic, no agents, no git
// factory-s2 dispatch --handover h.json --plan plan.json      prints teams and waves
// factory-s2 run --handover <h.json | designc handover dir> --repo . [--registry ../registry] [--base main] [--run-id id] [--github] [--since <previous run id>]
// factory-s2 rerun --handover <h.json | dir> --plan plan.json --task T-001 [--k 3] --repo . [--base main]   k fresh attempts, no merge

var cmd = args.FirstOrDefault() ?? "help";
var opts = ParseOptions(args.Skip(1).ToArray());

try
{
    switch (cmd)
    {
        case "validate":
        {
            var validation = Validate(opts);
            if (validation.IsValid) { Console.WriteLine("plan is valid"); return 0; }
            Console.WriteLine("plan rejected:");
            foreach (var e in validation.Errors) Console.WriteLine($"  - {e}");
            return 1;
        }

        case "dispatch":
        {
            var validation = Validate(opts);
            if (!validation.IsValid)
            {
                foreach (var e in validation.Errors) Console.Error.WriteLine($"  - {e}");
                return 1;
            }
            Console.WriteLine(Core.DispatchJson(Core.Dispatch(validation.Plan)));
            return 0;
        }

        case "run":
        {
            var settings = FactorySettings.FromEnvironment(
                Required(opts, "repo"), opts.GetValueOrDefault("registry"), opts.GetValueOrDefault("base"));
            var runId = opts.GetValueOrDefault("run-id") ?? $"{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            Directory.CreateDirectory(settings.StateDir);

            var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "");
            // Traces leave the process only when OTEL_EXPORTER_OTLP_ENDPOINT is set (Langfuse or any
            // OTLP collector); disposing the provider at the end of the run flushes them.
            using var tracing = FactoryTelemetry.CreateTracerProvider(env);
            var services = Services(settings, runId, env, opts.ContainsKey("github")
                ? new GhCliPullRequests(settings.RepoPath)
                : new DryRunPullRequests(Path.Combine(settings.StateDir, "runs", runId)));

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            Console.WriteLine($"run {runId}: re-run with --run-id {runId} to resume");
            var result = await WorkflowHost.RunAsync(
                S2Workflow.Build(services),
                new StartRun(runId, Path.GetFullPath(await HandoverInput.ResolveAsync(Required(opts, "handover"), settings.StateDir, cts.Token)),
                    opts.GetValueOrDefault("since")),
                new ConsoleHumanChannel(),
                cts.Token);

            Console.WriteLine(result is null ? "run ended without a result" : RunReport.Line(result));
            return result?.Status switch { "merged" or "auto_merge_enabled" => 0, "parked" => 3, _ => 1 };
        }

        case "rerun":
        {
            var settings = FactorySettings.FromEnvironment(
                Required(opts, "repo"), opts.GetValueOrDefault("registry"), opts.GetValueOrDefault("base"));
            Directory.CreateDirectory(settings.StateDir);
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            var handover = Core.ParseHandover(await File.ReadAllTextAsync(
                await HandoverInput.ResolveAsync(Required(opts, "handover"), settings.StateDir, cts.Token), cts.Token));
            var validation = Core.ValidatePlan(handover, await File.ReadAllTextAsync(Required(opts, "plan"), cts.Token));
            if (!validation.IsValid) throw new FactoryRejectedException("plan rejected", validation.Errors);
            var taskId = Required(opts, "task");
            var task = validation.Plan.Plan.Tasks.FirstOrDefault(t => t.Id.Value == taskId)
                ?? throw new ArgumentException($"--task {taskId} is not in the plan");
            var k = int.Parse(opts.GetValueOrDefault("k") ?? "3", System.Globalization.CultureInfo.InvariantCulture);
            var prefix = $"rerun-{taskId}-{DateTime.UtcNow:yyyyMMdd-HHmmss}";

            var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "");
            using var tracing = FactoryTelemetry.CreateTracerProvider(env);
            var services = Services(settings, prefix, env, new DryRunPullRequests(Path.Combine(settings.StateDir, "runs", prefix)));

            var report = await TaskRerun.RunAsync(services, handover, task, k, prefix, cts.Token);
            foreach (var a in report.Attempts)
                Console.WriteLine($"  {a.RunId}: {a.Outcome}, {a.Turns} turns, tokens {a.TokensIn}/{a.TokensOut}{(a.Failure is null ? "" : $" — {a.Failure}")}");
            Console.WriteLine(report.Line());
            return 0;
        }

        default:
            Console.WriteLine("usage: factory-s2 validate|dispatch|run|rerun  (see README)");
            return 2;
    }
}
catch (FactoryRejectedException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
catch (ArgumentException e)
{
    // A missing or bad option, or an unknown setting: a usage error, not a crash.
    Console.Error.WriteLine(e.Message);
    return 2;
}

/// Everything a run (or a rerun) needs, wired from the environment.
static FactoryServices Services(FactorySettings settings, string runId, Dictionary<string, string> env, IPullRequests pullRequests)
{
    var ledger = new JsonlLedger(Path.Combine(settings.StateDir, "ledger.jsonl"));
    // Agent code runs only in Docker; FACTORY_SANDBOX=local is the explicit, unisolated fallback.
    ISandboxFactory inner = (Environment.GetEnvironmentVariable("FACTORY_SANDBOX") ?? "docker") switch
    {
        "docker" => new DockerSandboxFactory(new DockerSandboxOptions(
            Image: Environment.GetEnvironmentVariable("FACTORY_RUNNER_IMAGE") ?? "factory-s2-runner:latest", NpmRebuild: settings.Npm?.Rebuild), ledger, runId),
        "local" => new LocalSandboxFactory(),
        var other => throw new ArgumentException($"FACTORY_SANDBOX: '{other}' is not docker|local"),
    };
    // Heavy commands share the machine's CPU across teams; a timeout is re-run once alone (FACTORY_HEAVY_SLOTS).
    ISandboxFactory sandboxes = new GatedSandboxFactory(inner, new HeavyGate(
        int.TryParse(Environment.GetEnvironmentVariable("FACTORY_HEAVY_SLOTS"), out var slots) && slots > 0 ? slots : HeavyGate.DefaultSlots));
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
    return new FactoryServices(
        settings,
        ledger,
        agents,
        configurator,
        git,
        new VerifyRunner(ledger, sandboxes, git),
        sandboxes,
        pullRequests,
        new AgentReviewer(agents, gateways.ReviewerIndependence, settings.CommandAllowList),
        mutation: new StrykerRunner(sandboxes));
}

static PlanValidation Validate(Dictionary<string, string> opts) =>
    Core.ValidatePlan(
        Core.ParseHandover(File.ReadAllText(Required(opts, "handover"))),
        File.ReadAllText(Required(opts, "plan")));

static string Required(Dictionary<string, string> opts, string name) =>
    opts.TryGetValue(name, out var v) ? v : throw new ArgumentException($"--{name} is required");

static Dictionary<string, string> ParseOptions(string[] a)
{
    var d = new Dictionary<string, string>();
    for (var i = 0; i < a.Length; i++)
    {
        if (!a[i].StartsWith("--")) continue;
        var key = a[i][2..];
        var hasValue = i + 1 < a.Length && !a[i + 1].StartsWith("--");
        d[key] = hasValue ? a[++i] : "true";
    }
    return d;
}
