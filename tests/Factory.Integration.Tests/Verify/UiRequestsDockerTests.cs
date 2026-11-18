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

/// F9 in the real runner image. Opt-in (FACTORY_TEST_DOCKER=1, FACTORY_CONTAINER_ENGINE=docker).
[Trait("needs", "docker")]
public sealed class UiRequestsDockerTests : IDisposable
{
    private static readonly bool Docker = Environment.GetEnvironmentVariable("FACTORY_TEST_DOCKER") == "1";
    private readonly string _state = Directory.CreateTempSubdirectory("ui-requests-state").FullName;
    private static readonly string Helper = Path.Combine("ui-contract", "preview", "vitest-ui.tsx");

    private static PlannedTask Writer(string id, string path) =>
        new(TaskId.NewTaskId(id), "title", "instructions", Core.ToList<ElementId>([]), Core.ToList([Plans.Pattern(path)]), Core.ToList<PathPattern>([]),
            Core.ToList<TaskId>([]), Core.ToList<DecisionId>([]), Core.ToList<VerifyEntry>([]));

    /// Approved pictures, rendered once per test class instance in the runner image.
    internal static async Task<Dictionary<string, byte[]>> RenderApprovedAsync()
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

    private sealed record Seeded(Handover Handover, GitWorkspace Git, string Integration, MemoryLedger Ledger);

    private int _n;

    private async Task<Seeded> SeedAsync(Dictionary<string, byte[]> previews, string? page, Func<string, string>? helper)
    {
        var fx = await UiPreviewFixture.CreateAsync();
        var state = Path.Combine(_state, "s" + ++_n);
        Directory.CreateDirectory(state);
        var h = UiHandoverDir.Create(previews);
        var json = await HandoverInput.ResolveAsync(h.Dir, state, default);
        var handover = Core.ParseHandover(await File.ReadAllTextAsync(json));
        var runDir = Path.Combine(state, "runs", "r1");
        HandoverInput.CopyUiPreviewsToRun(handover, json, runDir);
        var git = new GitWorkspace(fx.Repo, Path.Combine(state, "ws"));
        var integration = await git.EnsureIntegrationAsync("r1", "main", default);
        await UiContractSeed.SeedAsync(git, "r1", integration, handover, runDir, default);
        if (page is not null) File.Copy(Path.Combine(UiPreviewFixture.FixtureRoot, page, "BordereauxList.tsx"), Path.Combine(integration, "src", "BordereauxList.tsx"), overwrite: true);
        if (helper is not null) { var p = Path.Combine(integration, Helper); File.WriteAllText(p, helper(File.ReadAllText(p))); }
        return new(handover, git, integration, new MemoryLedger());
    }

    private static Task<List<VerifyOutcome>> RunAsync(Seeded s, string? runDir = null) =>
        new UiPageCheck(s.Ledger, new DockerSandboxFactory(new DockerSandboxOptions()), UiPageCheck.DefaultCommand, runDir: runDir)
            .RunAsync("r1", s.Handover, Core.ToList([Writer("T-1", "src/BordereauxList.tsx")]), s.Git, s.Integration, TimeSpan.FromMinutes(10), default);

    /// The raw vitest run of the page tests (default reporter, so the console output is in it).
    private static async Task<(int Exit, string Output)> VitestAsync(Seeded s)
    {
        await using var sb = await new DockerSandboxFactory(new DockerSandboxOptions()).ForTeamAsync("integration", s.Integration, default);
        var run = await sb.RunAsync("npx vitest run --project ui-page --reporter=default", s.Integration, TimeSpan.FromMinutes(10),
            new Dictionary<string, string> { ["CHROME_PATH"] = UiPreviewRunner.ChromePath, ["CI"] = "1" }, default);
        return (run.ExitCode, run.Output);
    }

    /// Slice 1 of F9: the kit's `onUnhandledRequest: "error"` is ignored by MSW 3 (the option is now `onUnhandledFrame`, default "warn"),
    /// so a page that calls an endpoint the spec does not list still passes. Recorded in Fixtures/msw/*.txt. Even the renamed option only
    /// logs an error and answers 500: the page swallows it and the tests pass, which is why the request log (slice 2) judges the run.
    [Fact]
    public async Task The_kits_onUnhandledRequest_is_ignored_by_msw_3_and_the_unlisted_call_only_warns()
    {
        if (!Docker || UiPreviewFixture.DesigncDir() is null) return;
        var previews = await RenderApprovedAsync();

        var kit = await VitestAsync(await SeedAsync(previews, "UnlistedCall", t => t.Replace("onUnhandledFrame", "onUnhandledRequest")));
        Assert.Equal(0, kit.Exit);
        Assert.Contains("[MSW] Warning: intercepted a request without a matching request handler", kit.Output);
        Assert.DoesNotContain("[MSW] Error:", kit.Output);
        Assert.Contains("GET /api/unlisted", kit.Output);

        var fixedRun = await VitestAsync(await SeedAsync(previews, "UnlistedCall", null));
        Assert.Equal(0, fixedRun.Exit);
        Assert.Contains("[MSW] Error: intercepted a request without a matching request handler", fixedRun.Output);
        Assert.Contains("GET /api/unlisted", fixedRun.Output);

        if (Environment.GetEnvironmentVariable("FACTORY_RECORD_MSW") == "1")
        {
            string Clean(string t) => System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(t, "\u001b\\[[0-9;]*m", ""), "localhost:[0-9]+", "localhost:<port>");
            var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "msw"));
            File.WriteAllText(Path.Combine(dir, "kit-onUnhandledRequest.txt"), Clean(kit.Output));
            File.WriteAllText(Path.Combine(dir, "fixed-onUnhandledFrame.txt"), Clean(fixedRun.Output));
        }
    }

    private static List<JsonElement> Log(Seeded s, string state) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(s.Integration, ".factory", "requests", "bordereaux", state + ".json"))).RootElement.EnumerateArray().ToList();

    /// Slice 2: the generated page test writes what MSW saw, per state, with allowWrite set in the fixture's vitest.config.ts.
    [Fact]
    public async Task The_page_test_writes_a_request_log_per_state_with_the_unlisted_call_marked_unhandled()
    {
        if (!Docker || UiPreviewFixture.DesigncDir() is null) return;
        var s = await SeedAsync(await RenderApprovedAsync(), "UnlistedCall", null);
        await RunAsync(s);

        var ready = Log(s, "ready");
        Assert.Contains(ready, e => e.GetProperty("method").GetString() == "GET" && e.GetProperty("path").GetString() == "/api/bordereaux"
            && e.GetProperty("matched").GetBoolean() && e.GetProperty("status").GetInt32() == 200);
        Assert.Contains(ready, e => e.GetProperty("path").GetString() == "/api/bordereaux/B-2" && e.GetProperty("matched").GetBoolean());
        Assert.Contains(ready, e => e.GetProperty("path").GetString() == "/api/unlisted" && !e.GetProperty("matched").GetBoolean());
        Assert.Contains(Log(s, "error"), e => e.GetProperty("path").GetString() == "/api/bordereaux" && e.GetProperty("status").GetInt32() == 500);
        Assert.Contains(Log(s, "loading"), e => e.GetProperty("path").GetString() == "/api/bordereaux");
    }

    /// Slice 3, done-when part 1: a page that calls an endpoint the spec does not list fails ui: with a `request` row naming it.
    [Fact]
    public async Task A_page_calling_an_unlisted_endpoint_fails_ui_with_a_request_row_and_the_real_page_passes()
    {
        if (!Docker || UiPreviewFixture.DesigncDir() is null) return;
        var previews = await RenderApprovedAsync();

        var good = await SeedAsync(previews, null, null);
        var goodRun = Directory.CreateTempSubdirectory("ui-good-run").FullName;
        var pass = Assert.Single(await RunAsync(good, goodRun));
        Assert.True(pass.ExitCode == 0, pass.OutputTail);
        Assert.False(Directory.Exists(Path.Combine(goodRun, "evidence", "ui")), "a passing run keeps no HTML report");
        Assert.DoesNotContain(good.Ledger.Events, e => e.Outcome == "evidence");
        Assert.DoesNotContain(good.Ledger.Events, e => e.Kind == LedgerKinds.UiCheckFailed);

        var bad = await SeedAsync(previews, "UnlistedCall", null);
        var fail = Assert.Single(await RunAsync(bad));
        Assert.Equal(1, fail.ExitCode);
        var rows = bad.Ledger.Events.Where(e => e.Kind == LedgerKinds.UiCheckFailed).Select(e => JsonDocument.Parse(e.Payload!).RootElement).ToList();
        var row = Assert.Single(rows);
        Assert.Equal("request", row.GetProperty("kind").GetString());
        Assert.Contains("GET /api/unlisted", row.GetProperty("detail").GetString());
        Assert.Contains("GET /api/unlisted", fail.OutputTail);
    }

    /// Slice 4, done-when part 2: the fixture HAR replays the page in the image (exit 0); one entry deleted and the page asks for what is missing (exit 1).
    [Fact]
    public async Task The_fixture_har_replays_the_page_with_exit_0_and_a_har_missing_an_entry_exits_1_without_touching_the_checkout()
    {
        if (!Docker) return;
        var fx = await UiPreviewFixture.CreateAsync();
        var harDir = Path.Combine(fx.Registry, "parity", "har");
        Directory.CreateDirectory(harDir);
        Directory.CreateDirectory(Path.Combine(fx.Registry, "parity", "scenarios"));
        File.WriteAllText(Path.Combine(fx.Registry, "parity", "scenarios", "list.yaml"), "id: list\nscreen: bordereaux\nsteps: []\n");
        var har = Path.Combine(UiPreviewFixture.FixtureRoot, "..", "har", "bordereaux.har");
        File.Copy(har, Path.Combine(harDir, "list-legacy.har"));
        var runner = new UiReplayRunner(new DockerSandboxFactory(new DockerSandboxOptions()));
        var request = new UiReplayRequest(fx.Registry, "list", fx.Repo, UiPageCheck.DefaultCommand);

        var ok = await runner.RunAsync(request, default);
        Assert.True(ok.ExitCode == 0, ok.Message);

        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(har))!;
        var entries = node["log"]!["entries"]!.AsArray();
        var detail = entries.First(e => e!["request"]!["url"]!.GetValue<string>().EndsWith("/api/bordereaux/17"))!;
        entries.Remove(detail);
        File.WriteAllText(Path.Combine(harDir, "list-legacy.har"), node.ToJsonString());
        var missing = await runner.RunAsync(request, default);
        Assert.Equal(1, missing.ExitCode);
        Assert.EndsWith("hold: GET /api/bordereaux/B-2", missing.Message);

        Assert.Empty((await ProcessRunner.RunAsync("git", ["status", "--porcelain"], fx.Repo, TimeSpan.FromSeconds(30))).Output.Trim());
        Assert.DoesNotContain("s2-replay-", string.Join("\n", (await ProcessRunner.RunAsync("git", ["worktree", "list"], fx.Repo, TimeSpan.FromSeconds(30))).Output));
    }

    [Fact]
    public void The_recorded_runs_say_the_same()
    {
        var dir = Path.Combine(UiPreviewFixture.FixtureRoot, "..", "msw");
        Assert.Contains("[MSW] Warning: intercepted", File.ReadAllText(Path.Combine(dir, "kit-onUnhandledRequest.txt")));
        Assert.Contains("[MSW] Error: intercepted", File.ReadAllText(Path.Combine(dir, "fixed-onUnhandledFrame.txt")));
        Assert.DoesNotContain("onUnhandledRequest: ", File.ReadAllText(Path.Combine(UiPreviewFixture.FixtureRoot, "app", "ui-contract", "preview", "vitest-ui.tsx")));
    }

    public void Dispose() => Directory.Delete(_state, recursive: true);
}
