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

    public async Task<ICommandSandbox> ForTeamAsync(string teamId, string worktree, CancellationToken ct) => new Sandbox(this, await inner.ForTeamAsync(teamId, worktree, ct));

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
            if (command.Contains("--project ui-page")) { owner.PageRuns++; return new(0, owner.PageReport(), TimeSpan.Zero, false); }
            if (command.Contains("--project ui-holdout"))
            {
                owner.HoldoutRuns.Add((workdir, File.Exists(Path.Combine(workdir, "ui-contract", "tests", owner.Screen + ".holdout.test.tsx")),
                    File.Exists(Path.Combine(workdir, "ui-contract", "holdout", owner.Screen + ".json")), command));
                return new(owner.HoldoutOutput is null ? 0 : 1, owner.HoldoutOutput ?? owner.HoldoutReport(workdir), TimeSpan.Zero, false);
            }
            return await real.RunAsync(command, workdir, timeout, env, ct);
        }
        public ValueTask DisposeAsync() => real.DisposeAsync();
    }
}
