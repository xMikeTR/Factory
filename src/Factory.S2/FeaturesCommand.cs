using System.Diagnostics;
using System.Text.RegularExpressions;
using Factory.Harness.Registry;
using Factory.S2.Git;

namespace Factory.S2;

public sealed record FeaturesOptions(string Registry, string Repo, string Designc, IReadOnlyList<string> Starts, string BaseUrl, int StartTimeoutSeconds,
    string Feature, string? Journey, string? Item, string Adapter, IReadOnlyList<(string Name, string Value)> Params, string? Out);

/// `factory-s2 features`: starts the app itself, runs each journey through designc's feature executor, reads back each journey's
/// effect, and records one `feature_result` per feature at the repo's HEAD through `designc record`. Never writes `ledger/` or
/// `features/` itself. Exit: 0 every recorded feature passed, 1 any failed, 3 could not run (or the app did not come up), 2 an error (nothing recorded).
public static class FeaturesCommand
{
    private static readonly string[] Valued = ["registry", "repo", "designc", "start", "base-url", "start-timeout", "feature", "journey", "item", "adapter", "param", "out"];

    /// `args` are the flags after the command word. `--start` and `--param` repeat.
    public static FeaturesOptions Parse(string[] args, string? designcFromEnv)
    {
        var single = new Dictionary<string, string>();
        var starts = new List<string>();
        var parms = new List<(string, string)>();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) throw new ArgumentException($"unexpected argument '{args[i]}'");
            var key = args[i][2..];
            if (!Valued.Contains(key)) throw new ArgumentException($"unknown flag --{key}");
            if (i + 1 >= args.Length) throw new ArgumentException($"--{key} needs a value");
            var value = args[++i];
            if (key == "start") starts.Add(value);
            else if (key == "param")
            {
                var eq = value.IndexOf('=');
                if (eq < 1) throw new ArgumentException($"--param needs name=value, got '{value}'");
                parms.Add((value[..eq], value[(eq + 1)..]));
            }
            else single[key] = value;
        }
        string Need(string k) => single.GetValueOrDefault(k) ?? throw new ArgumentException($"--{k} is required");
        var designc = single.GetValueOrDefault("designc") ?? designcFromEnv ?? throw new ArgumentException("--designc <designc app dir> (or FACTORY_DESIGNC_DIR) is required");
        if (starts.Count == 0) throw new ArgumentException("--start \"<command>\" is required (the Factory starts the app itself)");
        var timeout = 60;
        if (single.TryGetValue("start-timeout", out var t) && !int.TryParse(t, out timeout)) throw new ArgumentException("--start-timeout needs whole seconds");
        return new FeaturesOptions(Need("registry"), Need("repo"), designc, starts, Need("base-url"), timeout, single.GetValueOrDefault("feature") ?? "all",
            single.GetValueOrDefault("journey"), single.GetValueOrDefault("item"), single.GetValueOrDefault("adapter") ?? "web", parms, single.GetValueOrDefault("out"));
    }

    public static async Task<int> RunAsync(FeaturesOptions o, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var registry = Path.GetFullPath(o.Registry);
        var repo = Path.GetFullPath(o.Repo);
        var designc = Path.GetFullPath(o.Designc);
        var outDir = Path.GetFullPath(o.Out ?? Path.Combine(Path.GetTempPath(), $"factory-features-{DateTime.UtcNow:yyyyMMddTHHmmssZ}"));
        if (!Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https"))
        { stderr.WriteLine($"--base-url must be an http(s) URL, not '{o.BaseUrl}'"); return 2; }
        if (!Directory.Exists(registry)) { stderr.WriteLine($"no folder {registry}"); return 2; }
        if (!File.Exists(Path.Combine(designc, "scripts", "feature-run.js"))) { stderr.WriteLine($"{designc} is not a designc app folder (no scripts/feature-run.js)"); return 2; }
        foreach (var (what, dir) in new[] { ("the repo", repo), ("the registry", registry) })
            if (Inside(dir, outDir)) { stderr.WriteLine($"--out {outDir} is inside {what}; evidence is kept outside it"); return 2; }

        var git = new GitWorkspace(repo, Path.Combine(repo, ".factory"));
        if ((await git.ChangedPathsAsync(repo, ct)).Count > 0)
        { stderr.WriteLine("the repo has uncommitted changes: features are recorded against a commit, so commit first"); return 2; }
        var commit = await git.HeadAsync(repo, ct);

        var reg = await WorkRegistryReader.ReadAsync(registry, ct);
        var (picked, pickError) = FeatureRunner.Pick(reg.Features, o.Feature, o.Journey);
        var featureProblems = reg.Problems.Where(p => p.StartsWith("features/") && (o.Feature == "all" || p.StartsWith($"features/{o.Feature}."))).ToList();
        if (pickError is not null || featureProblems.Count > 0)
        {
            foreach (var p in featureProblems) stderr.WriteLine(p);
            if (pickError is not null) stderr.WriteLine(pickError);
            return 2;
        }
        var parms = o.Params.ToDictionary(p => p.Name, p => p.Value);

        Directory.CreateDirectory(outDir);
        var launcher = new AppLauncher(o.Starts, repo, o.BaseUrl, outDir, TimeSpan.FromSeconds(o.StartTimeoutSeconds));
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancelled.Cancel(); };
        EventHandler onExit = (_, _) => launcher.StopAsync().GetAwaiter().GetResult();
        Console.CancelKeyPress += onCancel;
        AppDomain.CurrentDomain.ProcessExit += onExit;
        try
        {
            var launched = await launcher.StartAsync(cancelled.Token);
            if (!launched.Up) { stderr.WriteLine(launched.Reason); return 3; }
            return await JudgeAndRecordAsync(o, picked!, parms, registry, designc, commit, outDir, stdout, stderr, cancelled.Token);
        }
        catch (OperationCanceledException) { stderr.WriteLine("cancelled; nothing recorded"); return 2; }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            AppDomain.CurrentDomain.ProcessExit -= onExit;
            await launcher.StopAsync();
        }
    }

    private static async Task<int> JudgeAndRecordAsync(FeaturesOptions o, IReadOnlyList<FeatureFile> picked, Dictionary<string, string> parms,
        string registry, string designc, string commit, string outDir, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        using var http = new HttpClient();
        var results = new List<(string Feature, IReadOnlyList<JourneyRun> Runs)>();
        foreach (var feature in picked)
        {
            var runs = new List<JourneyRun>();
            foreach (var journey in feature.Journeys.Where(j => o.Journey is null || j.Id == o.Journey))
            {
                var call = new JourneyCall(registry, feature.Id, journey.Id, o.Adapter, o.BaseUrl, o.Params, Path.Combine(outDir, "evidence"));
                var (exit, outText, errText) = await FeatureRunner.RunAsync(designc, call, ct);
                var (parsed, error) = FeatureRunner.Interpret(exit, outText, errText);
                if (parsed is null) { stderr.WriteLine($"{feature.Id}/{journey.Id}: {error}"); stderr.WriteLine("nothing recorded"); return 2; }
                var journeyRuns = parsed.SelectMany(p => p.Journeys).ToList() as IReadOnlyList<JourneyRun>;

                if (journey.Effect is { } effect && journeyRuns.Any(r => r.Outcome == "pass"))
                {
                    var (result, body) = await EffectReadBack.ReadAsync(http, o.BaseUrl, effect, parms, ct);
                    if (body is not null) await SaveEffectAsync(outDir, feature.Id, journey.Id, body, ct);
                    if (result.Outcome == "could_not_run") stderr.WriteLine(result.Message);
                    journeyRuns = FeatureRunner.ApplyEffect(journeyRuns, journey.Id, result, EffectReadBack.ExpandedStepCount(journey, feature.EntryPoints.Count > 0) + 1);
                }
                runs.AddRange(journeyRuns);
            }
            if (runs.Count > 0) results.Add((feature.Id, runs));
        }
        if (results.Count == 0) { stderr.WriteLine("no journey ran; nothing recorded"); return 2; }

        var rows = results.Select(r => FeatureResultRow.Build(r.Feature, commit, r.Runs, o.Item)).ToList();
        var (recorded, recordError) = await RecordAsync(designc, registry, rows, ct);
        if (!recorded) { stderr.WriteLine(recordError); stderr.WriteLine("nothing recorded"); return 2; }

        foreach (var (feature, runs) in results)
            stdout.WriteLine($"{feature}: {FeatureRunner.OutcomeOf(runs)} ({runs.Count} runs, {runs.Count(r => r.Outcome == "fail")} failed)");
        stdout.WriteLine($"evidence: {outDir}");
        var outcomes = results.Select(r => FeatureRunner.OutcomeOf(r.Runs)).ToList();
        return outcomes.Contains("fail") ? 1 : outcomes.Contains("could_not_run") ? 3 : 0;
    }

    private static async Task SaveEffectAsync(string outDir, string feature, string journey, string body, CancellationToken ct)
    {
        var dir = Path.Combine(outDir, "effects");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, $"{Safe(feature)}--{Safe(journey)}.json"), body, ct);
    }

    private static string Safe(string s) => Regex.Replace(s, "[^A-Za-z0-9._-]", "_");

    /// All rows in one batch through `python3 -m designc record <registry> --by <by> --json`, which validates them all or writes none.
    private static async Task<(bool Ok, string? Error)> RecordAsync(string designc, string registry, IReadOnlyList<string> rows, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("python3") { WorkingDirectory = Path.Combine(designc, "python"), RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-m", "designc", "record", registry, "--by", RegistryLedgerProjector.By, "--json" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("python3 did not start");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.StandardInput.WriteAsync(string.Join("\n", rows) + "\n");
        p.StandardInput.Close();
        await p.WaitForExitAsync(ct);
        return p.ExitCode == 0 ? (true, null) : (false, $"designc record refused the rows (exit {p.ExitCode}):\n{(await stdout).Trim()}\n{(await stderr).Trim()}".Trim());
    }

    private static bool Inside(string parent, string child)
    {
        var rel = Path.GetRelativePath(parent, child);
        return rel == "." || (!rel.StartsWith("..") && !Path.IsPathRooted(rel));
    }
}
