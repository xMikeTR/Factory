using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Harness.Registry;
using Factory.Harness.Sandbox;
using Factory.Integration.Tests.Support;
using Factory.S2;
using Factory.S2.Git;
using Factory.S2.Verify;
using Xunit;

namespace Factory.Integration.Tests.Verify;

using Core = Factory.Core.Core;

/// The holdout in the real runner image: a page that renders the spec's visible sample whatever the API sends (kit sabotage HardCodedSample)
/// passes the ui: check and fails the holdout, and the real page passes both. Opt-in (FACTORY_TEST_DOCKER=1, FACTORY_CONTAINER_ENGINE=docker).
[Trait("needs", "docker")]
public sealed class UiHoldoutDockerTests : IDisposable
{
    private static readonly bool Docker = Environment.GetEnvironmentVariable("FACTORY_TEST_DOCKER") == "1";
    private readonly string _state = Directory.CreateTempSubdirectory("ui-holdout-state").FullName;

    private static JsonElement Payload(LedgerEvent e) => JsonDocument.Parse(e.Payload!).RootElement;

    private static PlannedTask Writer(string id, string path) =>
        new(TaskId.NewTaskId(id), "title", "instructions", Core.ToList<ElementId>([]), Core.ToList([Plans.Pattern(path)]), Core.ToList<PathPattern>([]),
            Core.ToList<TaskId>([]), Core.ToList<DecisionId>([]), Core.ToList<VerifyEntry>([]));

    private static async Task<Dictionary<string, byte[]>> RenderApprovedAsync()
    {
        var designc = UiPreviewFixture.DesigncDir()!;
        var f = await UiPreviewFixture.CreateAsync();
        var result = await new UiPreviewRunner(new DockerSandboxFactory(new DockerSandboxOptions()))
            .RunAsync(new UiPreviewRequest(f.Registry, "bordereaux", f.Repo, designc, "factory-s2-runner:latest"), default);
        Assert.True(result.ExitCode == 0, result.Message);
        var dir = Path.Combine(f.Registry, "ui", "previews", "bordereaux");
        return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(dir, p).Replace('\\', '/'), File.ReadAllBytes);
    }

    [Fact]
    public async Task A_page_that_hard_codes_the_visible_sample_passes_ui_and_fails_the_holdout_and_the_real_page_passes_both()
    {
        if (!Docker || UiPreviewFixture.DesigncDir() is null) return;
        var fx = await UiPreviewFixture.CreateAsync();
        var h = UiHandoverDir.Create(await RenderApprovedAsync());
        var json = await HandoverInput.ResolveAsync(h.Dir, _state, default);
        var handover = Core.ParseHandover(await File.ReadAllTextAsync(json));
        var runDir = Path.Combine(_state, "runs", "r1");
        HandoverInput.CopyUiPreviewsToRun(handover, json, runDir);
        var git = new GitWorkspace(fx.Repo, Path.Combine(_state, "ws"));
        var integration = await git.EnsureIntegrationAsync("r1", "main", default);
        await UiContractSeed.SeedAsync(git, "r1", integration, handover, runDir, default);
        var ledger = new MemoryLedger();
        var sandboxes = new DockerSandboxFactory(new DockerSandboxOptions());
        var tasks = Core.ToList([Writer("T-1", "src/BordereauxList.tsx")]);
        var page = new UiPageCheck(ledger, sandboxes, UiPageCheck.DefaultCommand, writesResult: false);
        var holdout = new UiHoldoutCheck(ledger, sandboxes, fx.Registry, "npx vitest run --project ui-holdout --reporter=json");

        async Task<(bool Ui, HoldoutRun Holdout)> JudgeAsync()
        {
            await ProcessRunner.RunAsync("git", ["add", "-A"], integration, TimeSpan.FromSeconds(30));
            await ProcessRunner.RunAsync("git", ["-c", "user.name=t", "-c", "user.email=t@t", "commit", "-qm", "page", "--allow-empty"], integration, TimeSpan.FromSeconds(30));
            var ui = await page.RunAsync("r1", handover, tasks, git, integration, TimeSpan.FromMinutes(10), default);
            var ho = await holdout.RunAsync("r1", handover, tasks, git, TimeSpan.FromMinutes(10), default);
            return (ui.All(o => o.ExitCode == 0), ho);
        }

        var real = await JudgeAsync();
        Assert.True(real.Ui, "the real page passes ui:");
        Assert.Empty(real.Holdout.Outcomes);
        Assert.Equal("pass", real.Holdout.Screens["bordereaux"]);
        Assert.DoesNotContain(ledger.Events, e => e.Kind == LedgerKinds.HoldoutFailed);

        File.Copy(Path.Combine(UiPreviewFixture.FixtureRoot, "HardCodedSample", "BordereauxList.tsx"), Path.Combine(integration, "src", "BordereauxList.tsx"), overwrite: true);
        var cheat = await JudgeAsync();
        Assert.True(cheat.Ui, "the hard-coded page passes ui:, which only ever saw the visible sample");
        var outcome = Assert.Single(cheat.Holdout.Outcomes);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Equal("fail", cheat.Holdout.Screens["bordereaux"]);
        var rows = ledger.Events.Where(e => e.Kind == LedgerKinds.HoldoutFailed).ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, r =>
        {
            Assert.Equal("T-1", r.TaskId);
            Assert.Equal("bordereaux", Payload(r).GetProperty("screen").GetString());
            Assert.Equal(2, Payload(r).EnumerateObject().Count());
        });
        Assert.Contains(rows, r => Payload(r).GetProperty("state").GetString() == "ready");
        Assert.DoesNotContain("Baltic", outcome.OutputTail + string.Join("", ledger.Events.Select(e => e.Payload)) );
    }

    public void Dispose() => Directory.Delete(_state, recursive: true);
}
