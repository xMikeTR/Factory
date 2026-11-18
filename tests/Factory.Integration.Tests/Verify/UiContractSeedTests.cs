using Factory.Core;
using Factory.S2;
using Factory.S2.Git;
using Factory.Integration.Tests.Support;
using Xunit;

namespace Factory.Integration.Tests.Verify;

using Core = Factory.Core.Core;

/// The ui: check's files are put on the integration branch once, by the Factory, and any later change to the approved pictures is drift.
public sealed class UiContractSeedTests : IDisposable
{
    private readonly string _state = Directory.CreateTempSubdirectory("ui-seed-state").FullName;

    private sealed record Seeded(GitWorkspace Git, string Integration, string RunDir, Handover Handover);

    private async Task<Seeded> SeedAsync()
    {
        var fx = await UiPreviewFixture.CreateAsync();
        var h = UiHandoverDir.Create();
        var json = await HandoverInput.ResolveAsync(h.Dir, _state, default);
        var handover = Core.ParseHandover(await File.ReadAllTextAsync(json));
        var runDir = Path.Combine(_state, "runs", "r1");
        HandoverInput.CopyUiPreviewsToRun(handover, json, runDir);
        var git = new GitWorkspace(fx.Repo, Path.Combine(_state, "ws"));
        var integration = await git.EnsureIntegrationAsync("r1", "main", default);
        await UiContractSeed.SeedAsync(git, "r1", integration, handover, runDir, default);
        return new(git, integration, runDir, handover);
    }

    [Fact]
    public async Task Seeding_commits_the_spec_manifests_pictures_and_page_test_and_records_a_baseline()
    {
        var s = await SeedAsync();
        string P(string rel) => Path.Combine(s.Integration, "ui-contract", rel);
        Assert.True(File.Exists(P("specs/bordereaux.json")));
        Assert.True(File.Exists(P("manifests/DataTable.json")));
        Assert.True(File.Exists(P("previews/bordereaux/ready-chromium-linux.png")));
        Assert.Contains("test(\"ready aria\"", File.ReadAllText(P("tests/bordereaux.page.test.tsx")));
        Assert.Contains("../../src/BordereauxList", File.ReadAllText(P("tests/bordereaux.page.test.tsx")));
        Assert.True(File.Exists(Path.Combine(s.RunDir, "ui-baseline.json")));
        Assert.Empty(await s.Git.ChangedPathsAsync(s.Integration, default));
        Assert.Empty(await UiContractSeed.DriftAsync(s.Integration, s.RunDir, default));
    }

    [Fact]
    public async Task Seeding_twice_changes_nothing()
    {
        var s = await SeedAsync();
        var head = await s.Git.HeadAsync(s.Integration, default);
        await UiContractSeed.SeedAsync(s.Git, "r1", s.Integration, s.Handover, s.RunDir, default);
        Assert.Equal(head, await s.Git.HeadAsync(s.Integration, default));
    }

    [Fact]
    public async Task A_touched_picture_a_deleted_spec_and_an_added_placement_picture_are_all_drift()
    {
        var s = await SeedAsync();
        var png = Path.Combine(s.Integration, "ui-contract", "previews", "bordereaux", "ready-chromium-linux.png");
        File.AppendAllText(png, "x");
        File.Delete(Path.Combine(s.Integration, "ui-contract", "specs", "bordereaux.json"));
        File.WriteAllText(Path.Combine(s.Integration, "ui-contract", "previews", "bordereaux", "placements", "ready__extra-chromium-linux.png"), "x");
        var drift = await UiContractSeed.DriftAsync(s.Integration, s.RunDir, default);
        Assert.Contains(drift, l => l.StartsWith("modified") && l.Contains("ready-chromium-linux.png"));
        Assert.Contains(drift, l => l.StartsWith("missing") && l.Contains("specs/bordereaux.json"));
        Assert.Contains(drift, l => l.StartsWith("added") && l.Contains("ready__extra"));
    }

    [Fact]
    public async Task A_changed_vitest_config_is_drift()
    {
        var s = await SeedAsync();
        File.AppendAllText(Path.Combine(s.Integration, "vitest.config.ts"), "\n// loosened\n");
        Assert.Contains(await UiContractSeed.DriftAsync(s.Integration, s.RunDir, default), l => l.Contains("vitest.config.ts"));
    }

    [Fact]
    public async Task Only_seeded_files_still_equal_to_the_baseline_are_exempt_from_the_protected_path_block()
    {
        var s = await SeedAsync();
        var spec = "ui-contract/specs/bordereaux.json";
        var png = "ui-contract/previews/bordereaux/ready-chromium-linux.png";
        File.AppendAllText(Path.Combine(s.Integration, png), "x");
        var seeded = await UiContractSeed.UnchangedSeededAsync(s.Integration, s.RunDir, default);
        Assert.Contains(spec, seeded);
        Assert.DoesNotContain(png, seeded); // a seeded file that differs now still blocks
        Assert.DoesNotContain("ui-contract/specs/other.json", seeded); // a file never seeded still blocks
    }

    public void Dispose() => Directory.Delete(_state, recursive: true);
}
