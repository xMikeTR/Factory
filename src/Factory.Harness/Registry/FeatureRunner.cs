using System.Diagnostics;
using System.Text.Json;

namespace Factory.Harness.Registry;

/// One call of designc's `scripts/feature-run.js`: one journey of one feature (every entry point of it), on an app already up at BaseUrl.
public sealed record JourneyCall(string Registry, string Feature, string Journey, string Adapter, string BaseUrl,
    IReadOnlyList<(string Name, string Value)> Params, string EvidenceDir);

public sealed record RunFailure(int Step, string Kind, string Msg);

/// One run: a journey on one entry point (null when the feature has none). Outcome is `pass`, `fail` or `could_not_run`.
public sealed record JourneyRun(string Journey, string? Entry, string Outcome, IReadOnlyList<RunFailure> Failures);

public sealed record DesigncFeatureOutput(string Feature, string Outcome, IReadOnlyList<JourneyRun> Journeys);

/// Runs designc's feature executor the way ParityRecorder runs the parity recorder: designc owns the journeys, the ready check and
/// the outcomes; the Factory adds the effect read-back (EffectReadBack) and records the result. Writes are never allowed:
/// `--allow-writes` is not built.
public static class FeatureRunner
{
    public const int MaxFailures = 50;

    public static IReadOnlyList<string> Arguments(string script, JourneyCall c)
    {
        var args = new List<string> { script, "--registry", c.Registry, "--feature", c.Feature, "--journey", c.Journey,
            "--adapter", c.Adapter, "--base-url", c.BaseUrl };
        foreach (var (name, value) in c.Params) args.AddRange(["--param", $"{name}={value}"]);
        args.AddRange(["--out", c.EvidenceDir, "--json"]);
        return args;
    }

    /// Exit 0, 1 and 3 with designc's JSON are runs; exit 2, or output that is not that JSON, is an error carrying designc's words.
    public static (IReadOnlyList<DesigncFeatureOutput>? Results, string? Error) Interpret(int exitCode, string stdout, string stderr)
    {
        string Error(string why) => $"designc feature-run: {why}{(string.IsNullOrWhiteSpace(stderr) ? "" : "\n" + stderr.Trim())}";
        if (exitCode is not (0 or 1 or 3)) return (null, Error($"exit {exitCode}"));
        try
        {
            var root = JsonDocument.Parse(stdout).RootElement;
            if (root.ValueKind != JsonValueKind.Array) return (null, Error("output is not a JSON list"));
            var results = new List<DesigncFeatureOutput>();
            foreach (var f in root.EnumerateArray())
            {
                var outcome = f.GetProperty("outcome").GetString();
                if (!IsOutcome(outcome)) return (null, Error($"unknown outcome '{outcome}'"));
                var runs = new List<JourneyRun>();
                foreach (var j in f.GetProperty("journeys").EnumerateArray())
                {
                    var o = j.GetProperty("outcome").GetString();
                    if (!IsOutcome(o)) return (null, Error($"unknown outcome '{o}'"));
                    var entry = j.GetProperty("entry");
                    var failures = j.TryGetProperty("failures", out var fs) && fs.ValueKind == JsonValueKind.Array
                        ? fs.EnumerateArray().Select(x => new RunFailure(x.GetProperty("step").GetInt32(), x.GetProperty("kind").GetString()!, x.GetProperty("msg").GetString()!)).ToList()
                        : [];
                    runs.Add(new JourneyRun(j.GetProperty("journey").GetString()!, entry.ValueKind == JsonValueKind.String ? entry.GetString() : null, o!, failures));
                }
                results.Add(new DesigncFeatureOutput(f.GetProperty("feature").GetString()!, outcome!, runs));
            }
            return (results, null);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return (null, Error("output is not the expected JSON"));
        }
    }

    private static bool IsOutcome(string? o) => o is "pass" or "fail" or "could_not_run";

    /// `fail` if any run failed, else `could_not_run` if any could not run, else `pass` (designc's `journeys_shape`).
    public static string OutcomeOf(IEnumerable<JourneyRun> runs)
    {
        var all = runs.Select(r => r.Outcome).ToList();
        return all.Contains("fail") ? "fail" : all.Contains("could_not_run") ? "could_not_run" : "pass";
    }

    /// An effect that did not hold turns only this journey's passed runs: `fail` with one `effect` failure at `step`, or `could_not_run` with none.
    public static IReadOnlyList<JourneyRun> ApplyEffect(IReadOnlyList<JourneyRun> runs, string journey, EffectResult effect, int step) =>
        effect.Outcome == "pass" ? runs : runs.Select(r => r.Journey != journey || r.Outcome != "pass" ? r
            : effect.Outcome == "fail" ? r with { Outcome = "fail", Failures = [.. r.Failures, new RunFailure(step, "effect", effect.Message ?? "effect failed")] }
            : r with { Outcome = "could_not_run" }).ToList();

    /// The features and journeys to run: `all` or one id; a journey only where a feature has it. An id or journey nobody has is an error.
    public static (IReadOnlyList<FeatureFile>? Picked, string? Error) Pick(IReadOnlyList<FeatureFile> features, string feature, string? journey)
    {
        var picked = feature == "all" ? features.ToList() : features.Where(f => f.Id == feature).ToList();
        if (picked.Count == 0) return (null, feature == "all" ? "no features in features/" : $"no feature {feature} in features/");
        if (journey is not null)
        {
            picked = picked.Where(f => f.Journeys.Any(j => j.Id == journey)).ToList();
            if (picked.Count == 0) return (null, $"no journey {journey} in {(feature == "all" ? "any feature" : feature)}");
        }
        return (picked, null);
    }

    public static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(string designcDir, JourneyCall c, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("node") { WorkingDirectory = designcDir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in Arguments(Path.Combine(designcDir, "scripts", "feature-run.js"), c)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("node did not start");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, await stdout, await stderr);
    }
}

/// The `feature_result` ledger row (docs/feature-result.example.json), without `by`, which `designc record --by` fills.
public static class FeatureResultRow
{
    public static string Build(string feature, string commit, IReadOnlyList<JourneyRun> journeys, string? item)
    {
        var row = new Dictionary<string, object?>
        {
            ["kind"] = "feature_result",
            ["feature"] = feature,
            ["commit"] = commit,
            ["outcome"] = FeatureRunner.OutcomeOf(journeys),
            ["journeys"] = journeys.Select(j =>
            {
                var o = new Dictionary<string, object?> { ["journey"] = j.Journey, ["entry"] = j.Entry, ["outcome"] = j.Outcome };
                if (j.Failures.Count > 0) o["failures"] = j.Failures.Take(FeatureRunner.MaxFailures).Select(f => new Dictionary<string, object?> { ["step"] = f.Step, ["kind"] = f.Kind, ["msg"] = f.Msg });
                return o;
            }).ToList(),
        };
        if (item is not null) row["item"] = item;
        return JsonSerializer.Serialize(row);
    }
}
