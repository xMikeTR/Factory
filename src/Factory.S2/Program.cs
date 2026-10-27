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
using static Factory.S2.CliOptions;
using static Factory.S2.ServiceWiring;

// factory-s2 validate --handover h.json --plan plan.json      deterministic, no agents, no git
// factory-s2 dispatch --handover h.json --plan plan.json      prints teams and waves
// factory-s2 run --handover <h.json | designc handover dir> --repo . [--registry ../registry] [--base main] [--run-id id] [--github] [--since <previous run id>] [--repair <integration_failed run id> [--diagnose]]
// factory-s2 qa --repo . --run-id <id> [--base main]   follow-up QA: the run's protected tests, measured again, no author
// factory-s2 digest --registry ../registry [--repo .] [--base main] [--last-look <epoch ms>] [--wip 3] [--notify-state <file>: print NOTIFY for new Needs/Failed rows only]   Needs decision / Failed a gate / Done / Waiting
// factory-s2 pull-plan --registry ../registry [--repo .] [--base main] [--wip 3]   what the scheduler would pull now and why the rest waits (nothing is pulled)
// factory-s2 metrics --registry ../registry [--repo .] [--base main]   S4 work metrics: throughput, lead time, human wait, deferrals, re-check rate, escapes
// factory-s2 jira-mirror --registry ../registry [--snapshot jira.json] [--state f] [--apply | --applied] [--no-flag]   one-way ledger -> Jira plan; drift is flagged, never overwritten
// factory-s2 budget --repo . [--registry ../registry] [--no-report]   per-item token spend vs .factory/policy.yaml budget:; writes item_budget rows; exit 2 when an item is exceeded; `run` refuses a new run for an exceeded item (exit 4)
// factory-s2 parity --registry ../registry --item W-1 [--repo .] [--designc <dir>|FACTORY_DESIGNC_DIR] [--base-url <new app>] [--adapter web|fixture:<file>]   designc's recorder on the next side at the clean HEAD (appends parity_result), then parity-decisions; exit 0 pass, 1 fail, 3 could not run, 2 error
// factory-s2 parity-decisions --registry ../registry   one open needs_decision (A build like legacy / B ask the PO) per scenario whose latest parity_result failed; exit 2 when any was raised
// factory-s2 gate-shadow --cases cases.json [--repo .]   the QA gate's jira.transition policy against recorded kit decisions: logs what it would allow or deny (nothing enforced) and lists where it disagrees with the kit
// factory-s2 intake --registry ../registry --id BUG-1 --summary "..." --spec tests/acceptance/<element>/<AC>.spec.ts --origin spec_gap|design_error|translation_error|gate_gap --ref <requirement|decision|task|gate> --gate "<new or strengthened gate>" [--strengthens <gate>] [--caught-by pipeline]   closes a bug as an escape row
// factory-s2 ci-report --repo . --run-id <id> --check "<CI check>" --log <file | -> [--file a.cs,b.cs | --task T-001] [--commit sha]   a CI failure after the run ended, blamed on the tasks whose write paths own the files; then run --repair <id> (at most FACTORY_MAX_FIX_ROUNDS rounds, default 2)
// factory-s2 verify --repo . --run-id <id> --check "T-001: <check name>" --by <you> [--outcome done|deferred] [--note "why"]   records a manual check; run again with --run-id <id> to reach the merge (Tiered: --by must be an approver; deferred needs --note)
// factory-s2 run|rerun ... [--reference key=path]...   binds each read-only reference the handover names (a legacy app) to a folder outside the repo; agents read it with read_reference/list_reference and never write it
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
            // A brand-new project has no repository yet: create it with an empty baseline before anything reads it.
            if (repairOf is null
                && await GitWorkspace.EnsureRepoAsync(Required(opts, "repo"),
                    opts.GetValueOrDefault("base") ?? Environment.GetEnvironmentVariable("FACTORY_BASE_BRANCH") ?? "main",
                    CancellationToken.None) is { } created)
                Console.WriteLine(created);
            var settings = FactorySettings.FromEnvironment(
                Required(opts, "repo"), opts.GetValueOrDefault("registry"),
                repairOf is null ? opts.GetValueOrDefault("base") : GitWorkspace.IntegrationBranch(repairOf));
            settings = settings with { References = ReferenceFlags(args).Bindings };
            var runId = opts.GetValueOrDefault("run-id") ?? (repairOf is null ? $"{DateTime.UtcNow:yyyyMMdd-HHmmss}" : $"{repairOf}-r1");
            Directory.CreateDirectory(settings.StateDir);

            // result.json in the run folder says how this attempt ended and why, whatever the exit path.
            // An attempt that ended without reaching Finalize (crash, Ctrl+C, workflow error) is ended in the registry too.
            Task Ended(RunOutcome o) => Factory.Harness.Registry.RunAttempt.EndAsync(ProgressLedger(settings), runId, o.Status, o.Reason, CancellationToken.None);
            return await RunOutcomeFile.RecordAsync(settings.RunDir(runId), runId, async () =>
            {
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
            var parsed = Core.ParseHandover(await File.ReadAllTextAsync(handoverPath, cts.Token));
            RequireReferences(parsed, args, settings.RepoPath);
            RequireContracts(parsed, Environment.GetEnvironmentVariable("FACTORY_CONTRACTS"), Console.Error);

            // A new run for an item already over its token budget is refused (a resume of a started run is not new;
            // started means the workflow copied the handover, so a refused attempt's result.json does not count).
            if (!File.Exists(settings.HandoverCopyPath(runId)) && File.Exists(handoverPath))
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
                        return new RunOutcome("refused", refusal, 4);
                    }
                }
            }

            if (repairOf is not null && !File.Exists(settings.RepairPath(runId)))
            {
                var maxRounds = int.TryParse(Environment.GetEnvironmentVariable("FACTORY_MAX_FIX_ROUNDS"), out var mr) && mr > 0 ? mr : 2;
                if (Repair.RoundsRefusal(settings, repairOf, maxRounds) is { } tooMany)
                {
                    Console.Error.WriteLine("refused: " + tooMany);
                    return new RunOutcome("refused", tooMany, 1);
                }
                var repair = await Repair.FromFailedRunAsync(services, repairOf, cts.Token);
                if (repair is null)
                {
                    var nothing = $"run {repairOf} recorded no acceptance, token-lint or CI failure that names a task: nothing to repair";
                    Console.Error.WriteLine(nothing);
                    return new RunOutcome("refused", nothing, 1);
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
            return result is null
                ? new RunOutcome("no_result", "run ended without a result", 1)
                : new RunOutcome(result.Status, result.Detail, result.Status switch { "merged" or "auto_merge_enabled" or "ready_for_review" => 0, "parked" => 3, _ => 1 });
            }, Ended);
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
            // review; --judge also scores them with the model (a paid call, a gradient, never a gate). With --judge --item <id>
            // --registry <path> [--judge-name ux] [--threshold 3.5] the mean is also written as that item's `item_judge` row.
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
            if (opts.GetValueOrDefault("item") is { } judgedItem && opts.GetValueOrDefault("registry") is { } judgeRegistry && result.Scores.Count > 0)
            {
                // The judge's mean score for the item at HEAD, as an `item_judge` row (a gradient: never decides Done).
                var threshold = double.Parse(opts.GetValueOrDefault("threshold") ?? "3.5", System.Globalization.CultureInfo.InvariantCulture);
                var (score, pass) = UxReview.itemJudgement(threshold, Microsoft.FSharp.Collections.ListModule.OfSeq(result.Scores.Select(x => Tuple.Create(x.Shot, x.Score)))).Value;
                var commit = await services.Git.HeadAsync(settings.RepoPath, cts.Token);
                var rubricHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(UxReview.rubric)))[..16];
                await Factory.Harness.Registry.ItemJudgeReport.WriteAsync(judgeRegistry,
                    new Factory.Harness.Registry.ItemJudgement(judgedItem, opts.GetValueOrDefault("judge-name") ?? "ux", score, threshold,
                        commit, Roles.UxJudge, rubricHash), DateTimeOffset.UtcNow, cts.Token);
                Console.WriteLine($"item_judge {judgedItem}: {score:0.0} vs {threshold:0.0} ({(pass ? "pass" : "fail")}) at {commit[..Math.Min(8, commit.Length)]}");
            }
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

        case "parity":
        {
            // designc's recorder replays every parity scenario on the new app, labelled with the repo's HEAD, and appends
            // the parity_result rows itself; then uncovered failures become needs_decision rows. Work VM only.
            var repo = Path.GetFullPath(opts.GetValueOrDefault("repo") ?? ".");
            var designc = opts.GetValueOrDefault("designc") ?? Environment.GetEnvironmentVariable("FACTORY_DESIGNC_DIR")
                ?? throw new ArgumentException("--designc <designc app dir> (or FACTORY_DESIGNC_DIR) is required");
            var git = new Factory.S2.Git.GitWorkspace(repo, Path.Combine(repo, ".factory"));
            if ((await git.ChangedPathsAsync(repo, CancellationToken.None)).Count > 0)
            {
                Console.Error.WriteLine("the repo has uncommitted changes: parity is recorded against a commit, so commit first");
                return 2;
            }
            var registry = Path.GetFullPath(Required(opts, "registry"));
            var check = new Factory.Harness.Registry.ParityCheck(registry, await git.HeadAsync(repo, CancellationToken.None),
                Required(opts, "item"), opts.GetValueOrDefault("base-url"), opts.GetValueOrDefault("adapter"));
            var (recorded, output) = await Factory.Harness.Registry.ParityRecorder.RecordAsync(designc, check, CancellationToken.None);
            Console.Write(output);
            if (recorded == Factory.Harness.Registry.ParityRecording.Error)
            {
                Console.Error.WriteLine("parity recorder error (no baseline, bad scenario or the app did not open): nothing judged");
                return 2;
            }
            var raised = await Factory.Harness.Registry.ParityDecisionReport.RaiseAsync(registry, DateTimeOffset.UtcNow, CancellationToken.None);
            Console.WriteLine($"parity {recorded} at {check.Commit[..Math.Min(8, check.Commit.Length)]}; {raised} needs_decision raised");
            return recorded switch { Factory.Harness.Registry.ParityRecording.Pass => 0, Factory.Harness.Registry.ParityRecording.Fail => 1, _ => 3 };
        }

        case "parity-decisions":
        {
            // One open needs_decision per scenario whose latest parity_result failed: build it like legacy, or ask the PO.
            var raised = await Factory.Harness.Registry.ParityDecisionReport.RaiseAsync(
                Required(opts, "registry"), DateTimeOffset.UtcNow, CancellationToken.None);
            Console.WriteLine(raised == 0 ? "no new uncovered parity differences" : $"{raised} needs_decision raised for failed parity scenarios");
            return raised == 0 ? 0 : 2;
        }

        case "gate-shadow":
        {
            var settings = FactorySettings.FromEnvironment(opts.GetValueOrDefault("repo") ?? ".", null, null);
            var gate = new JiraTransitionGate(AcsToolPolicy.Load(Environment.GetEnvironmentVariable("FACTORY_POLICY_DIR") ?? AcsToolPolicy.DefaultDirectory),
                new JsonlLedger(Path.Combine(settings.StateDir, "ledger.jsonl")), enforce: false);
            var rows = await GateShadow.RunAsync(gate, "gate-shadow-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), await File.ReadAllTextAsync(Required(opts, "cases")));
            Console.Write(GateShadow.Render(rows));
            return rows.All(r => r.Agrees) ? 0 : 2;
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

        case "ci-report":
        {
            // CI failed after the run ended: blame the tasks that own the failing files; run --repair <run-id> then re-runs only those.
            var settings = FactorySettings.FromEnvironment(Required(opts, "repo"), null, opts.GetValueOrDefault("base"));
            var runId = Required(opts, "run-id");
            if (!File.Exists(settings.PlanPath(runId))) { Console.Error.WriteLine($"no run {runId} with a plan in {settings.StateDir}"); return 1; }
            var logPath = Required(opts, "log");
            var log = logPath == "-" ? await Console.In.ReadToEndAsync() : await File.ReadAllTextAsync(logPath);
            var services = Services(settings, runId, new Dictionary<string, string>(), new DryRunPullRequests(Path.Combine(settings.StateDir, "runs", runId)));
            var why = await CiReport.RecordAsync(services, runId, Required(opts, "check"), log,
                (opts.GetValueOrDefault("file") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                opts.GetValueOrDefault("task"), opts.GetValueOrDefault("commit"), CancellationToken.None);
            Console.WriteLine(why is null ? $"recorded; repair with: factory-s2 run --handover <handover> --repo . --repair {runId}" : $"not recorded: {why}");
            return why is null ? 0 : 1;
        }

        case "verify":
        {
            // A manual check a person did (or deferred) for a parked run; the next attempt of the same run id reads it at the merge.
            var settings = FactorySettings.FromEnvironment(Required(opts, "repo"), null, opts.GetValueOrDefault("base"));
            var runId = Required(opts, "run-id");
            if (!File.Exists(settings.PlanPath(runId))) { Console.Error.WriteLine($"no run {runId} with a plan in {settings.StateDir}"); return 1; }
            var plan = Core.ValidatePlan(Core.ParseHandover(File.ReadAllText(settings.HandoverCopyPath(runId))), File.ReadAllText(settings.PlanPath(runId)));
            if (!plan.IsValid) { Console.Error.WriteLine($"run {runId}: its stored plan no longer validates"); return 1; }
            var why = await ManualChecks.RecordAsync(new JsonlLedger(Path.Combine(settings.StateDir, "ledger.jsonl")), runId, Core.Dispatch(plan.Plan),
                (settings.MergePolicy ?? Core.DefaultMergePolicy).Enforcement, Required(opts, "check"), Required(opts, "by"),
                opts.GetValueOrDefault("outcome") ?? "done", opts.GetValueOrDefault("note"), CancellationToken.None);
            Console.WriteLine(why is null ? $"recorded; run again with --run-id {runId} to reach the merge" : $"not recorded: {why}");
            return why is null ? 0 : 1;
        }

        case "close":
        {
            // A person merged a review-mode run's PR: confirm the merge, run the suite on the merged commit, record it so the registry closes the handover.
            var settings = FactorySettings.FromEnvironment(Required(opts, "repo"), opts.GetValueOrDefault("registry"), opts.GetValueOrDefault("base"));
            var runId = Required(opts, "run-id");
            var github = opts.ContainsKey("github");
            var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "");
            var services = Services(settings, runId, env, new DryRunPullRequests(Path.Combine(settings.StateDir, "runs", runId)));
            var branch = GitWorkspace.IntegrationBranch(runId);
            var why = await RunClose.RecordAsync(services.Ledger, runId,
                async ct => github
                    ? await new GhCliPullRequests(settings.RepoPath).MergedPullRequestAsync(branch, ct) is { } url ? $"pr {url}" : null
                    : await RunClose.BranchMergedAsync(settings.RepoPath, branch, settings.BaseBranch, ct),
                async ct =>
                {
                    // Merged on GitHub: the merged commit is origin's base, not the local one.
                    if (github) await services.Git.FetchAsync(settings.BaseBranch, ct);
                    return await RunClose.CheckMergedAsync(services, runId, github ? $"origin/{settings.BaseBranch}" : settings.BaseBranch, ct);
                },
                opts.GetValueOrDefault("by"), canState: !github, CancellationToken.None);
            if (why is null && Directory.Exists(settings.RegistryPath))
                await Factory.Harness.Registry.RegistryProgress.SyncAsync(services.Ledger, settings.RegistryPath, runId,
                    Factory.Harness.Registry.HandoverRef.From(services.LoadHandover(runId)), CancellationToken.None);
            Console.WriteLine(why is null ? $"run {runId}: merge recorded; the handover is closed" : $"not recorded: {why}");
            // Complete (now or by an earlier close): its worktrees and local branches go; the ledger and run folder stay.
            if (await RunClose.CleanUpAsync(services.Ledger, services.Git, runId, CancellationToken.None))
                Console.WriteLine($"run {runId}: worktrees and local factory/{runId}/* branches removed");
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
            RequireReferences(handover, args, settings.RepoPath);
            settings = settings with { References = ReferenceFlags(args).Bindings };
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
            Console.WriteLine("usage: factory-s2 validate|dispatch|run|rerun|qa|digest|pull-plan|metrics|jira-mirror|budget|parity|parity-decisions|intake|ci-report|verify|repro|ux-review|compare-browser  (see README)");
            return 2;
    }
}
catch (FactoryRejectedException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
catch (Exception e) when (e is ArgumentException or FormatException or OverflowException)
{
    // A missing or bad option (a number that is not one, too), or an unknown setting: a usage error, not a crash.
    Console.Error.WriteLine(e.Message);
    return 2;
}
