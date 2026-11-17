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

/// UI mutants on the fixture screen. Without the runner image every mutant is could_not_run and nothing is faked; in the image the correct page
/// kills the kit's tone mutant. Docker cases are opt-in (FACTORY_TEST_DOCKER=1, FACTORY_CONTAINER_ENGINE=docker).
[Trait("needs", "docker")]
public sealed class UiMutantCheckTests : IDisposable
{
    private static readonly bool Docker = Environment.GetEnvironmentVariable("FACTORY_TEST_DOCKER") == "1";
    private readonly string _state = Directory.CreateTempSubdirectory("ui-mutant-state").FullName;

    private async Task<(GitWorkspace Git, Handover Handover, string Integration)> SeedAsync(IReadOnlyDictionary<string, byte[]>? previews = null)
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
        await ProcessRunner.RunAsync("git", ["add", "-A"], integration, TimeSpan.FromSeconds(30));
        await ProcessRunner.RunAsync("git", ["-c", "user.name=t", "-c", "user.email=t@t", "commit", "-qm", "page", "--allow-empty"], integration, TimeSpan.FromSeconds(30));
        return (git, handover, integration);
    }

    private static JsonElement Payload(LedgerEvent e) => JsonDocument.Parse(e.Payload!).RootElement;

    [Fact]
    public async Task Without_the_runner_image_every_mutant_is_could_not_run_and_the_row_says_so()
    {
        var s = await SeedAsync();
        var ledger = new MemoryLedger();
        var result = await new UiMutantCheck(ledger, new LocalSandboxFactory(), "x", 40).RunAsync("r1", s.Handover, s.Handover.DesignTokens, s.Git, TimeSpan.FromMinutes(1), default);

        Assert.All(result["bordereaux"], r => Assert.Equal("could_not_run", r.Status));
        var row = Assert.Single(ledger.Events, e => e.Stage == "ui_mutants" && e.Outcome == "could_not_run");
        Assert.Equal("bordereaux", Payload(row).GetProperty("screen").GetString());
        Assert.Equal(JsonValueKind.Null, Payload(row).GetProperty("score").ValueKind);
    }

    [Fact]
    public async Task The_cap_cuts_in_generate_order_and_the_cut_is_logged()
    {
        var s = await SeedAsync();
        var ledger = new MemoryLedger();
        var result = await new UiMutantCheck(ledger, new LocalSandboxFactory(), "x", 2).RunAsync("r1", s.Handover, s.Handover.DesignTokens, s.Git, TimeSpan.FromMinutes(1), default);

        Assert.Equal(2, result["bordereaux"].Count);
        var note = Assert.Single(ledger.Events, e => e.Outcome == "note");
        Assert.True(Payload(note).GetProperty("cut").GetInt32() > 0);
    }

    [Fact]
    public async Task In_the_runner_image_the_correct_page_kills_the_tone_mutant_on_its_placements()
    {
        if (!Docker || UiPreviewFixture.DesigncDir() is null) return;
        var designc = UiPreviewFixture.DesigncDir()!;
        var f = await UiPreviewFixture.CreateAsync();
        var sandboxes = new DockerSandboxFactory(new DockerSandboxOptions());
        var rendered = await new UiPreviewRunner(sandboxes).RunAsync(new UiPreviewRequest(f.Registry, "bordereaux", f.Repo, designc, "factory-s2-runner:latest"), default);
        Assert.True(rendered.ExitCode == 0, rendered.Message);
        var dir = Path.Combine(f.Registry, "ui", "previews", "bordereaux");
        var s = await SeedAsync(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(dir, p).Replace('\\', '/'), File.ReadAllBytes));
        var ledger = new MemoryLedger();
        // The fixture handover freezes no tokens; give it the registry's, as a designc handover with tokens would.
        var tokens = Microsoft.FSharp.Core.FSharpOption<string>.Some(await File.ReadAllTextAsync(Path.Combine(f.Registry, "design-tokens", "tokens.json")));

        var result = await new UiMutantCheck(ledger, sandboxes, "npx vitest run --project ui-page --reporter=json", 40)
            .RunAsync("r1", s.Handover, tokens, s.Git, TimeSpan.FromMinutes(10), default);

        var tone = Assert.Single(result["bordereaux"], r => r.Id == "premium:tone:neutral->danger");
        Assert.Equal("killed", tone.Status);
        Assert.True(tone.KilledBy!.Contains("empty placement-pixels"), "killed by: " + string.Join(", ", tone.KilledBy));
        Assert.Contains(ledger.Events, e => e.Stage == "ui_mutants" && Payload(e).GetProperty("screen").GetString() == "bordereaux");
        Assert.Equal("clean", (await ProcessRunner.RunAsync("git", ["status", "--porcelain"], s.Integration, TimeSpan.FromSeconds(30))).Output.Trim() == "" ? "clean" : "dirty");
    }

    public void Dispose() => Directory.Delete(_state, recursive: true);
}
