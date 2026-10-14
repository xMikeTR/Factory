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

// factory-s2 validate --handover h.json --plan plan.json      deterministic, no agents, no git
// factory-s2 dispatch --handover h.json --plan plan.json      prints teams and waves
// factory-s2 run --handover <h.json | designc handover dir> --repo . [--registry ../registry] [--base main] [--run-id id] [--github] [--since <previous run id>] [--repair <integration_failed run id> [--diagnose]]
// factory-s2 qa --repo . --run-id <id> [--base main]   follow-up QA: the run's protected tests, measured again, no author
// factory-s2 digest --registry ../registry [--repo .] [--base main] [--last-look <epoch ms>] [--wip 3] [--notify-state <file>: print NOTIFY for new Needs/Failed rows only]   Needs decision / Failed a gate / Done / Waiting
// factory-s2 pull-plan --registry ../registry [--repo .] [--base main] [--wip 3]   what the scheduler would pull now and why the rest waits (nothing is pulled)
// factory-s2 metrics --registry ../registry [--repo .] [--base main]   S4 work metrics: throughput, lead time, human wait, deferrals, re-check rate, escapes
// factory-s2 jira-mirror --registry ../registry [--snapshot jira.json] [--state f] [--apply | --applied] [--no-flag]   one-way ledger -> Jira plan; drift is flagged, never overwritten
// factory-s2 budget --repo . [--registry ../registry] [--no-report]   per-item token spend vs .factory/policy.yaml budget:; writes item_budget rows; exit 2 when an item is exceeded; `run` refuses a new run for an exceeded item (exit 4)
// factory-s2 intake --registry ../registry --id BUG-1 --summary "..." --spec tests/acceptance/<element>/<AC>.spec.ts --origin spec_gap|design_error|translation_error|gate_gap --ref <requirement|decision|task|gate> --gate "<new or strengthened gate>" [--strengthens <gate>] [--caught-by pipeline]   closes a bug as an escape row
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
            // --repair <failed run id>: cut from that run's integration branch, re-run only the tasks its failures blame.
            var repairOf = opts.GetValueOrDefault("repair");
            var settings = FactorySettings.FromEnvironment(
                Required(opts, "repo"), opts.GetValueOrDefault("registry"),
                repairOf is null ? opts.GetValueOrDefault("base") : GitWorkspace.IntegrationBranch(repairOf));
            var runId = opts.GetValueOrDefault("run-id") ?? (repairOf is null ? $"{DateTime.UtcNow:yyyyMMdd-HHmmss}" : $"{repairOf}-r1");
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

            var handoverPath = Path.GetFullPath(await HandoverInput.ResolveAsync(Required(opts, "handover"), settings.StateDir, cts.Token));

            // A new run for an item already over its token budget is refused (a resume of a started run is not new).
            if (!Directory.Exists(settings.RunDir(runId)) && File.Exists(handoverPath))
            {
                using var handoverDoc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(handoverPath, cts.Token));
                if (handoverDoc.RootElement.TryGetProperty("workItem", out var wi) && wi.GetString() is { Length: > 0 } workItem)
                {
                    var refusal = await Factory.Harness.Registry.ItemBudgetReport.GateAsync(
                        settings.StateDir, new JsonlLedger(Path.Combine(settings.StateDir, "ledger.jsonl")),
                        Factory.Harness.Policy.BudgetPolicyFile.Load(settings.RepoPath), workItem,
                        opts.GetValueOrDefault("registry"), DateTimeOffset.UtcNow, cts.Token);
                    if (refusal is not null)
                    {
                        Console.Error.WriteLine("refused: " + refusal);
                        return 4;
                    }
                }
            }

            if (repairOf is not null && !File.Exists(settings.RepairPath(runId)))
            {
                var repair = await Repair.FromFailedRunAsync(services, repairOf, cts.Token);
                if (repair is null)
                {
                    Console.Error.WriteLine($"run {repairOf} recorded no acceptance or token-lint failure that names a task: nothing to repair");
                    return 1;
                }
                if (opts.ContainsKey("diagnose"))
                {
                    // A read-only agent explains each blamed task's failure from its trace (a paid call, so opt-in).
                    await using var checkout = await services.Git.BaselineCheckoutAsync(runId, cts.Token, GitWorkspace.IntegrationBranch(repairOf));
                    repair = await Factory.S2.Quality.InvestigationRunner.DiagnoseAsync(services, repair, checkout.Path, cts.Token);
                }
                Directory.CreateDirectory(settings.RunDir(runId));
                await File.WriteAllTextAsync(settings.RepairPath(runId), System.Text.Json.JsonSerializer.Serialize(repair), cts.Token);
                Console.WriteLine($"repairing {repairOf}: re-running {string.Join(", ", repair.Feedback.Keys)}");
            }

            Console.WriteLine($"run {runId}: re-run with --run-id {runId} to resume");
            var result = await WorkflowHost.RunAsync(
                S2Workflow.Build(services),
                new StartRun(runId, handoverPath,
                    repairOf ?? opts.GetValueOrDefault("since")),
                new ConsoleHumanChannel(),
                cts.Token);

            Console.WriteLine(result is null ? "run ended without a result" : RunReport.Line(result));
            return result?.Status switch { "merged" or "auto_merge_enabled" => 0, "parked" => 3, _ => 1 };
        }

        case "digest":
        {
            var (digest, problems) = await Factory.Harness.Registry.WorkDigestReport.BuildAsync(
                Required(opts, "registry"), opts.GetValueOrDefault("repo"), opts.GetValueOrDefault("base") ?? "main",
                long.Parse(opts.GetValueOrDefault("last-look") ?? "0"), int.Parse(opts.GetValueOrDefault("wip") ?? "3"), CancellationToken.None);
            Console.Write(Factory.Harness.Registry.WorkDigestReport.Render(digest, problems));
            if (opts.GetValueOrDefault("notify-state") is { } statePath)
            {
                var seen = File.Exists(statePath) ? File.ReadAllLines(statePath).ToHashSet() : [];
                var fresh = DigestModule.newAlerts(Microsoft.FSharp.Collections.SetModule.OfSeq(seen), digest);
                foreach (var key in fresh) Console.WriteLine("NOTIFY " + key);
                File.AppendAllLines(statePath, fresh);
            }
            return digest.Failed.IsEmpty && problems.Count == 0 ? 0 : 1;
        }

        case "repro":
        {
            // factory-s2 repro --repo . --summary "<report>" [--run-id id] [--branch main]: a read-only agent reproduces a
            // reported escape and the steps are ledgered (`reproduction`). Intake still needs the failing spec. A paid call.
            var settings = FactorySettings.FromEnvironment(Required(opts, "repo"), opts.GetValueOrDefault("registry"), opts.GetValueOrDefault("base"));
            var runId = opts.GetValueOrDefault("run-id") ?? $"repro-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            Directory.CreateDirectory(settings.StateDir);
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "");
            using var tracing = FactoryTelemetry.CreateTracerProvider(env);
            var services = Services(settings, runId, env, new DryRunPullRequests(Path.Combine(settings.StateDir, "runs", runId)));
            await using var checkout = await services.Git.BaselineCheckoutAsync(runId, cts.Token, opts.GetValueOrDefault("branch") ?? settings.BaseBranch);
            var steps = await Factory.S2.Quality.InvestigationRunner.ReproduceAsync(
                services, runId, Required(opts, "summary"), checkout.Path, cts.Token);
            Console.WriteLine(steps ?? "not reproduced: the agent recorded no steps");
            return steps is null ? 1 : 0;
        }

        case "ux-review":
        {
            // factory-s2 ux-review --repo . --handover <h> [--judge] [--run-id id]: freeze-time screenshots for one human
            // review; --judge also scores them with the model (a paid call, a gradient, never a gate).
            var settings = FactorySettings.FromEnvironment(Required(opts, "repo"), opts.GetValueOrDefault("registry"), opts.GetValueOrDefault("base"));
            if (settings.ScreenshotCommand is not { } shotCommand)
            {
                Console.Error.WriteLine("FACTORY_SCREENSHOT_CMD is not set: the command that writes <element>__<state>__<breakpoint>.png files to $FACTORY_SCREENSHOT_DIR");
                return 1;
            }
            var runId = opts.GetValueOrDefault("run-id") ?? $"ux-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            Directory.CreateDirectory(settings.StateDir);
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            var handover = Core.ParseHandover(await File.ReadAllTextAsync(
                await HandoverInput.ResolveAsync(Required(opts, "handover"), settings.StateDir, cts.Token), cts.Token));
            var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "");
            using var tracing = FactoryTelemetry.CreateTracerProvider(env);
            var services = Services(settings, runId, env, new DryRunPullRequests(Path.Combine(settings.StateDir, "runs", runId)));
            var result = await Factory.S2.Quality.UxReviewRunner.RunAsync(
                services, handover, runId, shotCommand, settings.RepoPath, opts.ContainsKey("judge"), cts.Token);
            Console.Write(Factory.S2.Quality.UxReviewRunner.Render(result));
            Console.WriteLine($"screenshots kept in {Path.Combine(settings.RunDir(runId), "ux-screenshots")}");
            return result.Shots.Count > 0 ? 0 : 1;
        }

        case "compare-browser":
        {
            // factory-s2 compare-browser --repo . --runs id1,id2,...   (each run's acting turns say which browser tool it had)
            var settings = FactorySettings.FromEnvironment(Required(opts, "repo"), null, null);
            var ledger = new JsonlLedger(Path.Combine(settings.StateDir, "ledger.jsonl"));
            var stats = await Factory.Harness.Registry.BrowserToolComparison.CompareAsync(ledger, Required(opts, "runs").Split(',', StringSplitOptions.RemoveEmptyEntries), CancellationToken.None);
            Console.Write(Factory.Harness.Registry.BrowserToolComparison.Render(stats));
            return 0;
        }

        case "pull-plan":
        {
            var plan = await Factory.Harness.Registry.WorkDigestReport.PullPlanAsync(
                Required(opts, "registry"), opts.GetValueOrDefault("repo"), opts.GetValueOrDefault("base") ?? "main",
                int.Parse(opts.GetValueOrDefault("wip") ?? "3"), CancellationToken.None);
            Console.Write(Factory.Harness.Registry.WorkDigestReport.RenderPullPlan(plan));
            return 0;
        }

        case "jira-mirror":
        {
            // factory-s2 jira-mirror --registry r --snapshot jira.json [--state jira-mirrored.json] [--applied] [--no-flag]
            // snapshot = { "HRE-7": "In Progress" }, the issues' current statuses. Prints the transitions the ledger implies;
            // S2 writes nothing to Jira. Drift (a status the mirror did not write) is never overwritten and is flagged to a
            // person (item_awaiting_human) unless --no-flag. --applied records the planned transitions as mirrored.
            // With JIRA_URL + JIRA_TOKEN (+ JIRA_USER for Cloud) set, statuses are read over REST and --apply moves the
            // issues; without them --snapshot is required and nothing is written to Jira (a person or a browser does it).
            var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "");
            var rest = Factory.Harness.Registry.JiraRestClient.FromEnvironment(env);
            if (opts.ContainsKey("apply") && rest is null)
            {
                Console.Error.WriteLine("--apply needs JIRA_URL and JIRA_TOKEN (and JIRA_USER for Jira Cloud) in the environment");
                return 1;
            }
            Dictionary<string, string> snapshot;
            if (opts.TryGetValue("snapshot", out var snapshotPath))
                snapshot = await Factory.Harness.Registry.JiraMirrorReport.ReadMapAsync(snapshotPath, CancellationToken.None);
            else if (rest is not null)
            {
                snapshot = [];
                foreach (var k in await Factory.Harness.Registry.JiraMirrorReport.KeysAsync(Required(opts, "registry"), CancellationToken.None))
                    if (await rest.StatusAsync(k, CancellationToken.None) is { } s) snapshot[k] = s;
            }
            else
            {
                Console.Error.WriteLine("give --snapshot jira.json, or set JIRA_URL and JIRA_TOKEN to read statuses over REST");
                return 1;
            }
            var statePath = opts.GetValueOrDefault("state") ?? Path.Combine(Required(opts, "registry"), "ledger", "jira-mirrored.json");
            var mirrored = await Factory.Harness.Registry.JiraMirrorReport.ReadMapAsync(statePath, CancellationToken.None);
            var plan = await Factory.Harness.Registry.JiraMirrorReport.PlanAsync(
                Required(opts, "registry"), opts.GetValueOrDefault("repo"), opts.GetValueOrDefault("base") ?? "main", snapshot, mirrored, CancellationToken.None);
            Console.Write(Factory.Harness.Registry.JiraMirrorReport.Render(plan));
            var drift = plan.OfType<Factory.Core.MirrorAction.Drift>().Count();
            if (drift > 0 && !opts.ContainsKey("no-flag"))
                await Factory.Harness.Registry.JiraMirrorReport.FlagDriftAsync(Required(opts, "registry"), plan, DateTimeOffset.UtcNow, CancellationToken.None);
            if (opts.ContainsKey("apply") && rest is not null)
            {
                var (moved, failed) = await Factory.Harness.Registry.JiraMirrorReport.ApplyAsync(rest, plan, CancellationToken.None);
                foreach (var (k, s) in moved) { mirrored[k] = s; Console.WriteLine($"  moved  {k} -> {s}"); }
                foreach (var f in failed) Console.Error.WriteLine("  FAILED " + f);
                await File.WriteAllTextAsync(statePath, System.Text.Json.JsonSerializer.Serialize(mirrored));
                if (failed.Count > 0 && drift == 0) return 1;
            }
            if (opts.ContainsKey("applied"))
            {
                foreach (var t in plan.OfType<Factory.Core.MirrorAction.Transition>()) mirrored[t.key] = t.target;
                await File.WriteAllTextAsync(statePath, System.Text.Json.JsonSerializer.Serialize(mirrored));
            }
            return drift > 0 ? 2 : 0;
        }

        case "metrics":
        {
            var m = await Factory.Harness.Registry.WorkDigestReport.MetricsAsync(
                Required(opts, "registry"), opts.GetValueOrDefault("repo"), opts.GetValueOrDefault("base") ?? "main", CancellationToken.None);
            Console.Write(Factory.Harness.Registry.WorkDigestReport.RenderMetrics(m));
            return 0;
        }

        case "budget":
        {
            // Per-item token spend (every run whose handover names the item) against .factory/policy.yaml `budget:`.
            var settings = FactorySettings.FromEnvironment(
                Required(opts, "repo"), opts.GetValueOrDefault("registry"), opts.GetValueOrDefault("base"));
            var policy = Factory.Harness.Policy.BudgetPolicyFile.Load(Path.GetFullPath(Required(opts, "repo")));
            var spend = await Factory.Harness.Registry.ItemBudgetReport.SpendAsync(
                settings.StateDir, new JsonlLedger(Path.Combine(settings.StateDir, "ledger.jsonl")), CancellationToken.None);
            var readings = Factory.Harness.Registry.ItemBudgetReport.Readings(policy, spend);
            Console.Write(readings.Count == 0 ? "no item has a budget limit (set budget: in .factory/policy.yaml)\n"
                : Factory.Harness.Registry.ItemBudgetReport.Render(readings));
            if (opts.TryGetValue("registry", out var registry) && !opts.ContainsKey("no-report"))
                await Factory.Harness.Registry.ItemBudgetReport.WriteAsync(registry, readings, DateTimeOffset.UtcNow, CancellationToken.None);
            return readings.Any(r => r.Status.IsExceeded) ? 2 : 0;
        }

        case "intake":
        {
            var why = await Factory.Harness.Registry.BugIntake.RecordAsync(
                Required(opts, "registry"),
                new Factory.Harness.Registry.BugIntakeRequest(
                    Required(opts, "id"), Required(opts, "summary"), Required(opts, "spec"), Required(opts, "origin"),
                    Required(opts, "ref"), opts.GetValueOrDefault("gate") ?? "", opts.GetValueOrDefault("strengthens"),
                    opts.GetValueOrDefault("caught-by")),
                DateTimeOffset.UtcNow, CancellationToken.None);
            Console.WriteLine(why is null ? $"escape {opts["id"]} recorded" : $"not closed: {why}");
            return why is null ? 0 : 1;
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

        case "qa":
        {
            var settings = FactorySettings.FromEnvironment(
                Required(opts, "repo"), opts.GetValueOrDefault("registry"), opts.GetValueOrDefault("base"));
            var runId = Required(opts, "run-id");
            Directory.CreateDirectory(settings.StateDir);
            var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "");
            var services = Services(settings, runId, env, new DryRunPullRequests(Path.Combine(settings.StateDir, "runs", runId)));
            if (services.Qa is not AgentQaRunner qa) throw new FactoryRejectedException("no QA environment: the repo has no .factory/qa-env.yaml", []);
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            var run = await qa.RerunAsync(runId, services.Git.IntegrationPath(runId), cts.Token);
            if (run.TimedOut) { Console.WriteLine("QA timed out: not evidence either way"); return 3; }
            if (run.Tests.Count == 0) { Console.WriteLine("no QA tests recorded for this run"); return 1; }
            var findings = Qa.judge(Core.ToList(run.Requirements), Core.ToList(run.Tests));
            Console.WriteLine(findings.IsEmpty ? $"QA clean: {run.Tests.Count} tests" : QaGate.Brief(findings));
            return findings.IsEmpty ? 0 : 1;
        }

        default:
            Console.WriteLine("usage: factory-s2 validate|dispatch|run|rerun|qa  (see README)");
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
    var flaky = settings.QualityPolicy?.Flaky ?? [];
    ISandboxFactory sandboxes = new GatedSandboxFactory(inner, new HeavyGate(
        int.TryParse(Environment.GetEnvironmentVariable("FACTORY_HEAVY_SLOTS"), out var slots) && slots > 0 ? slots : HeavyGate.DefaultSlots),
        isFlaky: command => flaky.Any(command.Contains),
        onFlakyRetry: command => ledger.AppendAsync(new LedgerEvent(runId, "S2", "flaky_retry", LedgerKinds.Verify, Outcome: "re-run alone", Payload: System.Text.Json.JsonSerializer.Serialize(new { command })), default).GetAwaiter().GetResult());
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
