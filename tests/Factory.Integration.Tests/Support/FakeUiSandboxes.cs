using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Harness.Sandbox;

namespace Factory.Integration.Tests.Support;

/// Stands in for the runner image without Docker: reports itself as "docker", answers the ui: page test run with an all-pass report and
/// the holdout run with whatever the test scripted, and hands every other command to the real (local) sandbox.
public sealed class FakeUiSandboxes(ISandboxFactory inner, string[] states) : ISandboxFactory
{
    public string Screen { get; init; } = "bordereaux";
    /// States the holdout run reports as failed (assertion `holdout_failed`).
    public HashSet<string> HoldoutFailing { get; } = [];
    /// Null: the holdout run writes a report. Else: the holdout run's whole output, whatever it is (no report, garbage).
    public string? HoldoutOutput { get; set; }
    /// What the holdout run saw in its working directory: the generated test and the sample were there while it ran.
    public List<(string Workdir, bool Test, bool Sample, string Command)> HoldoutRuns { get; } = [];
    public int PageRuns { get; private set; }
    /// Acceptance holdout specs (`*.holdout-<n>.spec.ts`) whose run is reported failed, by file name.
    public HashSet<string> AcceptanceHoldoutFailing { get; } = [];
    /// What the acceptance holdout run saw: the spec files that were in its working directory, and the command.
    public List<(string Workdir, string[] Files, string Command)> AcceptanceHoldoutRuns { get; } = [];

    public async Task<ICommandSandbox> ForTeamAsync(string teamId, string worktree, CancellationToken ct) => new Sandbox(this, await inner.ForTeamAsync(teamId, worktree, ct));

    /// What the generated page test writes: one request per endpoint the spec lists, matched, as a faithful page would make them.
    private void WriteRequestLog(string workdir)
    {
        var spec = Path.Combine(workdir, "ui-contract", "specs", Screen + ".json");
        if (!File.Exists(spec)) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(spec));
        var rows = new List<object>();
        if (doc.RootElement.TryGetProperty("data", out var data))
            foreach (var d in data.EnumerateObject())
                if (d.Value.TryGetProperty("source", out var src) && src.GetString()!.Split(' ') is [var m, var path])
                    rows.Add(new { method = m, path = path.Replace(":id", "1"), matched = true, body = (string?)null, status = 200 });
        var dir = Path.Combine(workdir, ".factory", "requests", Screen);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ready.json"), JsonSerializer.Serialize(rows));
    }

    private string PageReport() => JsonSerializer.Serialize(new
    {
        testResults = new[] { new { assertionResults = states.SelectMany(s => new[] { "aria", "pixels", "placements" }.Select(k =>
            new { ancestorTitles = new[] { Screen + " page" }, title = $"{s} {k}", status = "passed", failureMessages = Array.Empty<string>() })).ToArray() } },
    });

    /// A runner that echoes the sample it was given into its failure messages, as a real assertion diff would.
    private string HoldoutReport(string workdir)
    {
        var sampleFile = Path.Combine(workdir, "ui-contract", "holdout", Screen + ".json");
        var echo = File.Exists(sampleFile) ? File.ReadAllText(sampleFile) : "";
        return JsonSerializer.Serialize(new
    {
        testResults = new[] { new { assertionResults = states.Select(s => HoldoutFailing.Contains(s)
            ? new { ancestorTitles = new[] { Screen + " holdout" }, title = s, status = "failed", failureMessages = new[] { $"holdout_failed: {Screen}/{s}: the tree differs: SECRET-ROW-DATA {echo}" } }
            : new { ancestorTitles = new[] { Screen + " holdout" }, title = s, status = "passed", failureMessages = Array.Empty<string>() }).ToArray() } },
        });
    }

    private sealed class Sandbox(FakeUiSandboxes owner, ICommandSandbox real) : ICommandSandbox
    {
        public string Kind => "docker";
        public async Task<ProcessResult> RunAsync(string command, string workdir, TimeSpan timeout, IReadOnlyDictionary<string, string> env, CancellationToken ct)
        {
            if (command.Contains("--project ui-page")) { owner.PageRuns++; owner.WriteRequestLog(workdir); return new(0, owner.PageReport(), TimeSpan.Zero, false); }
            if (command.Contains("--project ui-holdout"))
            {
                owner.HoldoutRuns.Add((workdir, File.Exists(Path.Combine(workdir, "ui-contract", "tests", owner.Screen + ".holdout.test.tsx")),
                    File.Exists(Path.Combine(workdir, "ui-contract", "holdout", owner.Screen + ".json")), command));
                return new(owner.HoldoutOutput is null ? 0 : 1, owner.HoldoutOutput ?? owner.HoldoutReport(workdir), TimeSpan.Zero, false);
            }
            if (command.Contains(".holdout-") && command.Contains("playwright"))
            {
                var specs = Directory.EnumerateFiles(Path.Combine(workdir, "tests", "acceptance"), "*.holdout-*.spec.ts", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(workdir, f).Replace('\\', '/')).Order().ToArray();
                owner.AcceptanceHoldoutRuns.Add((workdir, specs, command));
                var report = JsonSerializer.Serialize(new { suites = specs.Select(f => new { file = f, specs = new[] { new { ok = !owner.AcceptanceHoldoutFailing.Contains(Path.GetFileName(f)), file = f } } }).ToArray() });
                var echo = string.Concat(specs.Select(f => File.ReadAllText(Path.Combine(workdir, f))));
                var path = Path.Combine(workdir, env["PLAYWRIGHT_JSON_OUTPUT_NAME"]);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, report, ct);
                var failed = owner.AcceptanceHoldoutFailing.Count > 0;
                return new(failed ? 1 : 0, failed ? "Error: expected text but got: " + echo : "", TimeSpan.Zero, false);
            }
            return await real.RunAsync(command, workdir, timeout, env, ct);
        }
        public ValueTask DisposeAsync() => real.DisposeAsync();
    }
}
