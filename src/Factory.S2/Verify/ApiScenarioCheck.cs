using System.Text.Json;
using System.Xml.Linq;
using Factory.Core;
using Factory.Harness;
using Factory.Harness.Registry;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;

namespace Factory.S2.Verify;

/// The generated API tests (X4): runs `generated/contracts/api-tests` in each serving repo's integration checkout, once, after the repo's own suite.
/// A failed example is an `api_scenario_failed` row per task of the serving element; each scenario gets an `api-scenario:<id>` verify row.
/// The test project is the agent's: it must compile the generated folder, implement IScenarioAdapter, and reference a JUnit logger
/// (the suite writes to $FACTORY_TEST_RESULTS, as every suite does); without results the check could not run, which is not a pass.
public sealed class ApiScenarioCheck(FactoryServices svc)
{
    private const string Stage = "integration_verify";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(30);

    public static string Command(string project) =>
        $"dotnet test \"{project}\" --filter \"{ApiScenarios.filter}\" --logger \"junit;LogFilePath=$PWD/{VerifyRunner.ResultsPath}\"";

    private sealed record Item(ApiScenario Scenario, ElementInterface Serving, string Repo);

    public async Task<List<VerifyOutcome>> RunAsync(string runId, Handover handover, IEnumerable<PlannedTask> tasks, CancellationToken ct)
    {
        var outcomes = new List<VerifyOutcome>();
        var specs = handover.Elements.ToDictionary(e => e.Id.Value);
        var items = new List<Item>();
        foreach (var a in handover.ApiScenarios.Where(Codegen.hasGeneratedExamples))
            if (Codegen.apiServing(handover, a) is { IsOk: true } ok && specs.TryGetValue(ok.ResultValue.Element, out var el))
                items.Add(new Item(a, ok.ResultValue, FSharpOption<string>.get_IsSome(el.Repo) ? el.Repo.Value : "."));
        var taskList = tasks.ToList();

        foreach (var group in items.GroupBy(i => i.Repo).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var git = group.Key != "." && svc.RepoGits.TryGetValue(group.Key, out var g) ? g : svc.Git;
            var dir = git.IntegrationPath(runId);
            // Nothing was seeded (codegen off, or this repo is not generated): there is nothing to judge.
            if (!Directory.Exists(Path.Combine(dir, ApiScenarios.folder))) continue;
            var commit = await git.HeadAsync(dir, ct);
            var name = svc.RepoGits.Count > 0 ? $"api-scenarios ({group.Key})" : "api-scenarios";
            outcomes.Add(await RepoAsync(runId, name, dir, commit, group.ToList(), taskList, ct));
        }
        return outcomes;
    }

    private async Task<VerifyOutcome> RepoAsync(string runId, string name, string dir, string commit, List<Item> items, List<PlannedTask> tasks, CancellationToken ct)
    {
        var failures = new List<(Item Item, int Example, string Title, string Kind, string Message)>();
        var ran = new HashSet<string>();
        string? couldNotRun = null;
        var tail = "";
        long ms = 0;

        var projects = Projects(dir);
        if (projects.Count != 1)
        {
            var wiring = projects.Count == 0
                ? $"no test project compiles {ApiScenarios.folder}/**: create or wire one in the serving repo and implement IScenarioAdapter there"
                : $"{projects.Count} projects could compile {ApiScenarios.folder}/** ({string.Join(", ", projects)}): exactly one test project must";
            foreach (var i in items)
                foreach (var e in i.Scenario.Examples.Where(e => FSharpOption<ApiRequest>.get_IsSome(e.Request)))
                    failures.Add((i, e.Index, e.Title, "seed", ApiScenarios.message(i.Scenario.Id, e.Index, e.Title, "wiring: " + wiring)));
            tail = "wiring: " + wiring;
        }
        else
        {
            var results = Path.Combine(dir, VerifyRunner.ResultsPath);
            var resultsDir = Path.GetDirectoryName(results)!;
            if (Directory.Exists(resultsDir)) Directory.Delete(resultsDir, recursive: true);
            try
            {
                await using var sandbox = await svc.Sandboxes.ForTeamAsync("integration", dir, ct);
                var command = Command(projects[0]);
                var r = await sandbox.RunAsync(command, dir, Timeout,
                    new Dictionary<string, string> { ["FACTORY_RUN_ID"] = runId, ["FACTORY_TEST_RESULTS"] = VerifyRunner.ResultsPath }, ct);
                ms = (long)r.Duration.TotalMilliseconds;
                tail = r.Tail(2000);
                if (File.Exists(results) && !r.TimedOut)
                {
                    try
                    {
                        foreach (var c in XDocument.Parse(await File.ReadAllTextAsync(results, ct)).Descendants("testcase").Where(c => c.Element("skipped") is null))
                        {
                            var parsed = ApiScenarios.parseDisplayName(c.Attribute("name")?.Value ?? "");
                            if (!FSharpOption<Tuple<string, int>>.get_IsSome(parsed)) continue;
                            var (id, index) = (parsed.Value.Item1, parsed.Value.Item2);
                            var item = items.FirstOrDefault(i => i.Scenario.Id == id);
                            if (item is null) continue;
                            ran.Add($"{id}#{index}");
                            if ((c.Element("failure") ?? c.Element("error")) is { } f)
                            {
                                var message = (f.Attribute("message")?.Value is { Length: > 0 } m ? m : f.Value).Trim();
                                var title = item.Scenario.Examples.FirstOrDefault(e => e.Index == index)?.Title ?? "";
                                failures.Add((item, index, title, ApiScenarios.classify(message), message));
                            }
                        }
                    }
                    catch (System.Xml.XmlException) { couldNotRun = "the test results are not readable"; }
                }
                else
                    couldNotRun = r.TimedOut ? "the generated API tests timed out" : "the generated API tests wrote no results (did the test project build, and does it reference a JUnit logger?)";
                if (couldNotRun is null && r.ExitCode != 0 && failures.Count == 0 && ran.Count == 0)
                    couldNotRun = "no generated API test ran (does the test project compile " + ApiScenarios.folder + "/**?)";
            }
            finally
            {
                if (Directory.Exists(resultsDir)) Directory.Delete(resultsDir, recursive: true);
            }
        }

        foreach (var f in failures)
        {
            var element = f.Item.Serving.Element;
            var blamed = tasks.Where(t => t.Elements.Any(e => e.Value == element)).ToList();
            foreach (var t in blamed)
                await svc.Ledger.AppendAsync(new LedgerEvent(runId, "S2", Stage, LedgerKinds.ApiScenarioFailed, TaskId: t.Id.Value, Element: element, Outcome: "failed",
                    Payload: JsonSerializer.Serialize(new { scenario = f.Item.Scenario.Id, example = f.Example, title = f.Title, kind = f.Kind, message = f.Message, commit })), ct);
        }
        foreach (var i in items)
        {
            var mine = failures.Where(f => f.Item == i).ToList();
            var code = couldNotRun is not null && mine.Count == 0 ? 3 : mine.Count > 0 ? 1 : 0;
            await svc.Ledger.AppendAsync(new LedgerEvent(runId, "S2", Stage, LedgerKinds.Verify, Element: i.Serving.Element, Outcome: code == 0 ? "passed" : "failed",
                Payload: JsonSerializer.Serialize(new
                {
                    name = "api-scenario:" + i.Scenario.Id, ExitCode = code, commit,
                    failures = mine.Select(f => new { kind = f.Kind, criterion = $"{i.Scenario.Id} example {f.Example}" }),
                    reason = code == 3 ? couldNotRun : null,
                })), ct);
        }

        if (failures.Count > 0)
        {
            var lines = failures.Select(f => $"API scenario {f.Item.Scenario.Id} example {f.Example} ({f.Title}) failed: {f.Message}");
            return new VerifyOutcome(name, 1, ms, string.Join("\n", lines));
        }
        return couldNotRun is not null ? new VerifyOutcome(name, 3, ms, "could_not_run: " + couldNotRun + (tail.Length > 0 ? "\n" + tail : "")) : new VerifyOutcome(name, 0, ms, tail);
    }

    /// The repo's csproj files that compile the generated folder: it sits under the project's directory, or the project names it.
    private static List<string> Projects(string dir) =>
        Directory.EnumerateFiles(dir, "*.csproj", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
            .Where(f => !f.Split('/').Any(s => s is "bin" or "obj" or "node_modules" or ".git"))
            .Where(f =>
            {
                var home = f.Contains('/') ? f[..f.LastIndexOf('/')] : "";
                return home.Length == 0 || (ApiScenarios.folder + "/").StartsWith(home + "/", StringComparison.Ordinal)
                    || File.ReadAllText(Path.Combine(dir, f)).Contains("api-tests", StringComparison.Ordinal);
            })
            .Order(StringComparer.Ordinal)
            .ToList();
}
