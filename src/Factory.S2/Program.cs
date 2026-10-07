using Factory.Core;
using Factory.Harness;
using Factory.Harness.Governance;
using Factory.Harness.Sandbox;
using Factory.Harness.Telemetry;
using Factory.S2;
using Factory.S2.Git;
using Factory.S2.Verify;

// factory-s2 validate --handover h.json --plan plan.json      deterministic, no agents, no git
// factory-s2 dispatch --handover h.json --plan plan.json      prints teams and waves
// factory-s2 run --handover <h.json | designc handover dir> --repo . [--registry ../registry] [--base main] [--run-id id] [--github]

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

            var ledger = new JsonlLedger(Path.Combine(settings.StateDir, "ledger.jsonl"));
            // Traces leave the process only when OTEL_EXPORTER_OTLP_ENDPOINT is set (Langfuse or any
            // OTLP collector); disposing the provider at the end of the run flushes them.
            var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "");
            using var tracing = FactoryTelemetry.CreateTracerProvider(env);
            // Agent code runs only in Docker; FACTORY_SANDBOX=local is the explicit, unisolated fallback.
            ISandboxFactory sandboxes = (Environment.GetEnvironmentVariable("FACTORY_SANDBOX") ?? "docker") switch
            {
                "docker" => new DockerSandboxFactory(new DockerSandboxOptions(
                    Image: Environment.GetEnvironmentVariable("FACTORY_RUNNER_IMAGE") ?? "factory-s2-runner:latest"), ledger, runId),
                "local" => new LocalSandboxFactory(),
                var other => throw new ArgumentException($"FACTORY_SANDBOX: '{other}' is not docker|local"),
            };
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
            var services = new FactoryServices(
                settings,
                ledger,
                new FactoryAgents(GatewayOptions.FromEnvironment(env), toolPolicy, ledger, FactoryTelemetry.CaptureContent(env)),
                configurator,
                new GitWorkspace(settings.RepoPath, settings.StateDir),
                new VerifyRunner(ledger, sandboxes),
                sandboxes,
                opts.ContainsKey("github")
                    ? new GhCliPullRequests(settings.RepoPath)
                    : new DryRunPullRequests(Path.Combine(settings.StateDir, "runs", runId)));

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            Console.WriteLine($"run {runId}: re-run with --run-id {runId} to resume");
            var result = await WorkflowHost.RunAsync(
                S2Workflow.Build(services),
                new StartRun(runId, Path.GetFullPath(await HandoverInput.ResolveAsync(Required(opts, "handover"), settings.StateDir, cts.Token))),
                new ConsoleHumanChannel(),
                cts.Token);

            Console.WriteLine(result is null ? "run ended without a result" : RunReport.Line(result));
            return result?.Status switch { "merged" or "auto_merge_enabled" => 0, "parked" => 3, _ => 1 };
        }

        default:
            Console.WriteLine("usage: factory-s2 validate|dispatch|run  (see README)");
            return 2;
    }
}
catch (FactoryRejectedException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
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
