using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Harness.Sandbox;
using Factory.Integration.Tests.Support;
using Factory.S2;
using Factory.S2.Git;
using Factory.S2.Teams;
using Factory.S2.Verify;
using Microsoft.Extensions.AI;
using Xunit;

namespace Factory.Integration.Tests.Verify;

using Core = Factory.Core.Core;

/// Failure feedback from the recorded real Vitest 5.1 runs (Fixtures/ui-feedback), through the ui: check and into the repair brief, with no Docker:
/// a stand-in runner image answers the page run with the recorded terminal output, JSON report and screenshots.
public sealed class UiFeedbackTests : IDisposable
{
    private static readonly string Fx = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ui-feedback");
    private readonly string _state = Directory.CreateTempSubdirectory("ui-feedback").FullName;

    /// Reports itself as docker; the page run writes what a recorded run left in the app (`.vitest/json/output.json`, the screenshots, an HTML report) and prints the recorded output.
    private sealed class Recorded(string name, bool html = true) : ISandboxFactory
    {
        public Task<ICommandSandbox> ForTeamAsync(string teamId, string worktree, CancellationToken ct) => Task.FromResult<ICommandSandbox>(new Box(name, html));
        private sealed class Box(string name, bool html) : ICommandSandbox
        {
            public string Kind => "docker";
            public Task<ProcessResult> RunAsync(string command, string workdir, TimeSpan timeout, IReadOnlyDictionary<string, string> env, CancellationToken ct)
            {
                var v = Path.Combine(workdir, ".vitest");
                Directory.CreateDirectory(Path.Combine(v, "json"));
                File.Copy(Path.Combine(Fx, name + ".report.json"), Path.Combine(v, "json", "output.json"));
                if (Directory.Exists(Path.Combine(Fx, name + ".attachments")))
                    foreach (var f in Directory.EnumerateFiles(Path.Combine(Fx, name + ".attachments"), "*", SearchOption.AllDirectories))
                    {
                        var to = Path.Combine(v, "attachments", Path.GetRelativePath(Path.Combine(Fx, name + ".attachments"), f));
                        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                        File.Copy(f, to);
                    }
                if (html)
                {
                    File.WriteAllText(Path.Combine(v, "index.html"), "<html/>");
                    Directory.CreateDirectory(Path.Combine(v, "ui"));
                    File.WriteAllText(Path.Combine(v, "ui", "html.meta.json.gz"), "x");
                }
                return Task.FromResult(new ProcessResult(1, File.ReadAllText(Path.Combine(Fx, name + ".output.txt")), TimeSpan.Zero, false));
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private async Task<(TeamHarness H, string RunDir)> RunAsync(string name, bool html = true)
    {
        var h = await TeamHarness.CreateAsync(new ScriptedChatClient(), new Limits(8, 3));
        var fx = await UiPreviewFixture.CreateAsync();
        var ui = UiHandoverDir.Create();
        var json = await HandoverInput.ResolveAsync(ui.Dir, _state, default);
        var handover = Core.ParseHandover(await File.ReadAllTextAsync(json));
        var runDir = h.Services.Settings.RunDir(TeamHarness.RunId);
        HandoverInput.CopyUiPreviewsToRun(handover, json, runDir);
        var git = new GitWorkspace(fx.Repo, Path.Combine(_state, "ws"));
        var integration = await git.EnsureIntegrationAsync(TeamHarness.RunId, "main", default);
        await UiContractSeed.SeedAsync(git, TeamHarness.RunId, integration, handover, runDir, default);
        var task = new PlannedTask(TaskId.NewTaskId("T-1"), "t", "i", Core.ToList<ElementId>([]), Core.ToList([Plans.Pattern("src/BordereauxList.tsx")]),
            Core.ToList<PathPattern>([]), Core.ToList<TaskId>([]), Core.ToList<DecisionId>([]), Core.ToList<VerifyEntry>([]));
        var outcomes = await new UiPageCheck(h.Services.Ledger, new Recorded(name, html), UiPageCheck.DefaultCommand, writesResult: false, runDir: runDir)
            .RunAsync(TeamHarness.RunId, handover, Core.ToList([task]), git, integration, TimeSpan.FromMinutes(1), default);
        Assert.Equal(1, Assert.Single(outcomes).ExitCode);
        return (h, runDir);
    }

    [Fact]
    public async Task An_ARIA_failing_run_gives_the_diff_text_and_the_timeline_and_no_picture()
    {
        var (h, runDir) = await RunAsync("aria");
        using var _ = h;

        var repair = await Repair.FromFailedRunAsync(h.Services, TeamHarness.RunId, default);

        Assert.NotNull(repair);
        var text = repair.Feedback["T-1"];
        Assert.Contains("- - heading \"Bordereaux\" [level=1]", text);
        Assert.Contains("+ - heading \"Bordereaux list\" [level=1]", text);
        Assert.Contains("react.render", text);
        Assert.Null(repair.Images);
        File.WriteAllText(h.Services.Settings.RepairPath(TeamHarness.RunId), JsonSerializer.Serialize(repair));
        Assert.Empty(Repair.Images(h.Services.Settings, TeamHarness.RunId, "T-1"));
        Assert.Empty(Directory.Exists(Path.Combine(runDir, "evidence", "ui", "bordereaux", "ready")) ? Directory.GetFiles(Path.Combine(runDir, "evidence", "ui", "bordereaux", "ready")) : []);
        var brief = new TaskBrief("stable", text, Repair.Images(h.Services.Settings, TeamHarness.RunId, "T-1"));
        Assert.DoesNotContain(brief.Messages.SelectMany(m => m.Contents), c => c is DataContent);
    }

    [Fact]
    public async Task A_pixel_only_failure_gives_the_diff_images_and_the_repair_message_carries_them()
    {
        var (h, runDir) = await RunAsync("pixels");
        using var _ = h;

        var repair = await Repair.FromFailedRunAsync(h.Services, TeamHarness.RunId, default);

        Assert.NotNull(repair);
        Assert.True(repair.Feedback["T-1"].Contains("ARIA matches the spec; the look differs"), repair.Feedback["T-1"]);
        var images = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string[]>>(repair.Images)["T-1"];
        Assert.Equal(3, images.Length); // ready, filtered, empty: one placement diff each
        Assert.All(images, i => { Assert.StartsWith("evidence/ui/bordereaux/", i); Assert.True(File.Exists(Path.Combine(runDir, i))); Assert.Contains("-diff-", i); });
        Assert.True(File.Exists(Path.Combine(runDir, images[0].Replace("-diff-", "-actual-"))), "the actual picture is kept beside the diff");

        File.WriteAllText(h.Services.Settings.RepairPath(TeamHarness.RunId), JsonSerializer.Serialize(repair)); // the next run reads this file; Of names the run whose evidence it is
        var bytes = Repair.Images(h.Services.Settings, TeamHarness.RunId, "T-1");
        var brief = new TaskBrief("stable", repair.Feedback["T-1"], bytes);
        Assert.Equal(3, brief.Messages.SelectMany(m => m.Contents).OfType<DataContent>().Count());
        Assert.All(brief.Messages.SelectMany(m => m.Contents).OfType<DataContent>(), c => Assert.Equal("image/png", c.MediaType));
    }

    [Fact]
    public async Task The_HTML_report_is_kept_as_evidence_with_a_ui_evidence_row()
    {
        var (h, runDir) = await RunAsync("pixels");
        using var _ = h;
        Assert.True(File.Exists(Path.Combine(runDir, "evidence", "ui", "bordereaux", "html", "index.html")));
        var row = Assert.Single(h.Ledger.Events, e => e.Kind == LedgerKinds.Verify && e.Outcome == "evidence");
        Assert.Equal("ui", row.Stage);
        var p = JsonDocument.Parse(row.Payload!).RootElement;
        Assert.Equal("bordereaux", p.GetProperty("screen").GetString());
        Assert.Equal("evidence/ui/bordereaux/html", p.GetProperty("path").GetString());
    }

    [Fact]
    public async Task A_run_that_wrote_no_HTML_report_adds_no_evidence_row_and_never_fails()
    {
        var (h, runDir) = await RunAsync("aria", html: false);
        using var _ = h;
        Assert.DoesNotContain(h.Ledger.Events, e => e.Outcome == "evidence");
        Assert.False(Directory.Exists(Path.Combine(runDir, "evidence", "ui", "bordereaux", "html")));
    }

    [Fact]
    public void The_default_command_prints_failures_writes_the_json_report_and_the_html_report()
    {
        Assert.Contains("--reporter=default", UiPageCheck.DefaultCommand);
        Assert.Contains("--reporter=json", UiPageCheck.DefaultCommand);
        Assert.Contains("--reporter=html", UiPageCheck.DefaultCommand);
        Assert.Equal("npx vitest run --project ui-page --reporter=json", UiPageCheck.JsonOnly(UiPageCheck.DefaultCommand));
    }

    public void Dispose() => Directory.Delete(_state, recursive: true);
}
