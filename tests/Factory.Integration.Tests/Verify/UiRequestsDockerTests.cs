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

    private static Task<List<VerifyOutcome>> RunAsync(Seeded s) =>
        new UiPageCheck(s.Ledger, new DockerSandboxFactory(new DockerSandboxOptions()), UiPageCheck.DefaultCommand)
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
