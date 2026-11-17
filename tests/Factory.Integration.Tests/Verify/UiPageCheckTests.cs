using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Harness.Sandbox;
using Factory.Integration.Tests.Support;
using Factory.S2;
using Factory.S2.Git;
using Factory.S2.Verify;
using Xunit;

namespace Factory.Integration.Tests.Verify;

using Core = Factory.Core.Core;

/// The ui: check on a seeded integration branch: one ui_result per screen, ui_check_failed per failure and blamed task, and a run that
/// cannot run the page tests is could_not_run, never a pass. The Docker cases are opt-in (FACTORY_TEST_DOCKER=1, FACTORY_CONTAINER_ENGINE=docker).
[Trait("needs", "docker")]
public sealed class UiPageCheckTests : IDisposable
{
    private static readonly bool Docker = Environment.GetEnvironmentVariable("FACTORY_TEST_DOCKER") == "1";
    private readonly string _state = Directory.CreateTempSubdirectory("ui-check-state").FullName;

    private sealed record Seeded(GitWorkspace Git, string Integration, Handover Handover, MemoryLedger Ledger);

    private async Task<Seeded> SeedAsync(IReadOnlyDictionary<string, byte[]>? previews = null)
    {
        var fx = await UiPreviewFixture.CreateAsync();
        var h = UiHandoverDir.Create(previews);
        var json = await HandoverInput.ResolveAsync(h.Dir, _state, default);
        var handover = Core.ParseHandover(await File.ReadAllTextAsync(json));
        var runDir = Path.Combine(_state, "runs", "r1");
        HandoverInput.CopyUiPreviewsToRun(handover, json, runDir);
        var git = new GitWorkspace(fx.Repo, Path.Combine(_state, "ws"));
        var integration = await git.EnsureIntegrationAsync("r1", "main", default);
        await UiContractSeed.SeedAsync(git, "r1", integration, handover, runDir, default);
        return new(git, integration, handover, new MemoryLedger());
    }

    private static PlannedTask Writer(string id, string path) =>
        new(TaskId.NewTaskId(id), "title", "instructions", Core.ToList<ElementId>([]), Core.ToList([Plans.Pattern(path)]), Core.ToList<PathPattern>([]),
            Core.ToList<TaskId>([]), Core.ToList<DecisionId>([]), Core.ToList<VerifyEntry>([]));

    private static Task<List<VerifyOutcome>> RunAsync(Seeded s, ISandboxFactory sandboxes, params PlannedTask[] tasks) =>
        new UiPageCheck(s.Ledger, sandboxes, "npx vitest run --project ui-page --reporter=json")
            .RunAsync("r1", s.Handover, Core.ToList(tasks), s.Git, s.Integration, TimeSpan.FromMinutes(10), default);

    private static JsonElement Payload(Factory.Harness.LedgerEvent e) => JsonDocument.Parse(e.Payload!).RootElement;

    [Fact]
    public async Task Without_the_runner_image_the_screen_is_could_not_run_with_exit_3_and_never_a_pass()
    {
        var s = await SeedAsync();
        var outcomes = await RunAsync(s, new LocalSandboxFactory());

        var o = Assert.Single(outcomes);
        Assert.Equal("ui:bordereaux", o.Name);
        Assert.Equal(3, o.ExitCode);
        Assert.StartsWith("could_not_run:", o.OutputTail);
        Assert.DoesNotContain(s.Ledger.Events, e => e.Kind == LedgerKinds.UiCheckFailed);
        var result = Assert.Single(s.Ledger.Events, e => e.Kind == LedgerKinds.UiResult);
        Assert.Equal("could_not_run", Payload(result).GetProperty("outcome").GetString());
        Assert.Equal("W-ui", Payload(result).GetProperty("item").GetString());
        Assert.Equal(await s.Git.HeadAsync(s.Integration, default), Payload(result).GetProperty("commit").GetString());
    }

    [Fact]
    public async Task A_missing_page_file_fails_the_code_check_and_blames_the_task_that_writes_it()
    {
        var s = await SeedAsync();
        File.Delete(Path.Combine(s.Integration, "src", "BordereauxList.tsx"));
        var outcomes = await RunAsync(s, new LocalSandboxFactory(), Writer("T-1", "src/BordereauxList.tsx"), Writer("T-2", "src/other/**"));

        var o = Assert.Single(outcomes);
        Assert.Equal(1, o.ExitCode);
        Assert.Contains("code", o.OutputTail);
        var row = Assert.Single(s.Ledger.Events, e => e.Kind == LedgerKinds.UiCheckFailed);
        Assert.Equal("T-1", row.TaskId);
        Assert.Equal("code", Payload(row).GetProperty("kind").GetString());
        Assert.Equal("fail", Payload(Assert.Single(s.Ledger.Events, e => e.Kind == LedgerKinds.UiResult)).GetProperty("outcome").GetString());
    }

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
    public async Task The_correct_page_passes_every_state_and_the_wrong_tone_fails_placement_pixels_in_empty()
    {
        if (!Docker || UiPreviewFixture.DesigncDir() is null) return;
        var s = await SeedAsync(await RenderApprovedAsync());
        var sandboxes = new DockerSandboxFactory(new DockerSandboxOptions());
        var task = Writer("T-1", "src/BordereauxList.tsx");

        var pass = Assert.Single(await RunAsync(s, sandboxes, task));
        Assert.True(pass.ExitCode == 0, pass.OutputTail);
        Assert.Equal("pass", Payload(Assert.Single(s.Ledger.Events, e => e.Kind == LedgerKinds.UiResult)).GetProperty("outcome").GetString());

        File.Copy(Path.Combine(UiPreviewFixture.FixtureRoot, "WrongTone", "BordereauxList.tsx"), Path.Combine(s.Integration, "src", "BordereauxList.tsx"), overwrite: true);
        var fail = Assert.Single(await RunAsync(s, sandboxes, task));
        Assert.Equal(1, fail.ExitCode);
        var rows = s.Ledger.Events.Where(e => e.Kind == LedgerKinds.UiCheckFailed).Select(Payload).ToList();
        Assert.Contains(rows, p => p.GetProperty("kind").GetString() == "placement-pixels" && p.GetProperty("state").GetString() == "empty"
            && p.TryGetProperty("placement", out var pl) && pl.GetString() == "premium");
        Assert.Equal("fail", Payload(s.Ledger.Events.Last(e => e.Kind == LedgerKinds.UiResult)).GetProperty("outcome").GetString());
    }

    public void Dispose() => Directory.Delete(_state, recursive: true);
}
