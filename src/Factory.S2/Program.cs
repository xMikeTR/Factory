using Factory.Core;
using Factory.Harness;
using Factory.Harness.Sandbox;
using Factory.S2;
using Factory.S2.Git;
using Factory.S2.Verify;

// factory-s2 validate --handover h.json --plan plan.json      deterministic, no agents, no git
// factory-s2 dispatch --handover h.json --plan plan.json      prints teams and waves
// factory-s2 run --handover h.json --repo . [--registry ../registry] [--base main] [--run-id id] [--github]

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
            var services = new FactoryServices(
                settings,
                ledger,
                new FactoryAgents(GatewayOptions.FromEnvironment(), new RoleAllowListPolicy(), ledger),
                new HeuristicConfigurator(),
                new GitWorkspace(settings.RepoPath, settings.StateDir),
                new VerifyRunner(ledger, new LocalSandboxFactory()),
                opts.ContainsKey("github")
                    ? new GhCliPullRequests(settings.RepoPath)
                    : new DryRunPullRequests(Path.Combine(settings.StateDir, "runs", runId)));

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            Console.WriteLine($"run {runId}: re-run with --run-id {runId} to resume");
            var result = await WorkflowHost.RunAsync(
                S2Workflow.Build(services),
                new StartRun(runId, Path.GetFullPath(Required(opts, "handover"))),
                new ConsoleHumanChannel(),
                cts.Token);

            Console.WriteLine(result is null ? "run ended without a result" : $"{result.Status}\n{result.Detail}");
            return result?.Status is "merged" or "auto_merge_enabled" ? 0 : 1;
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
