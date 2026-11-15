using System.Text.Json;
using Factory.Harness.Sandbox;
using Factory.S2.Verify;
using Xunit;

namespace Factory.Integration.Tests.Verify;

/// `factory-s2 preview` in the real runner image. Opt-in: FACTORY_TEST_DOCKER=1 (and FACTORY_CONTAINER_ENGINE=docker where podman is not installed).
[Trait("needs", "docker")]
public class UiPreviewDockerTests
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("FACTORY_TEST_DOCKER") == "1";

    private static UiPreviewRunner Runner() => new(new DockerSandboxFactory(new DockerSandboxOptions()));
    private static UiPreviewRequest Request(UiPreviewFixture f, string designc) => new(f.Registry, "bordereaux", f.Repo, designc, "factory-s2-runner:latest");

    [Fact]
    public async Task Renders_five_page_pictures_and_thirty_placement_files_and_the_row_hashes_all_forty()
    {
        if (!Enabled || UiPreviewFixture.DesigncDir() is not { } designc) return;
        var f = await UiPreviewFixture.CreateAsync();

        var result = await Runner().RunAsync(Request(f, designc), default);

        Assert.True(result.ExitCode == 0, result.Message);
        var dir = Path.Combine(f.Registry, "ui", "previews", "bordereaux");
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(dir, p).Replace('\\', '/')).ToList();
        Assert.Equal(5, files.Count(x => x.EndsWith(".png") && !x.StartsWith("placements/")));
        Assert.Equal(5, files.Count(x => x.EndsWith(".aria.yml")));
        Assert.Equal(30, files.Count(x => x.StartsWith("placements/")));
        Assert.Equal(40, files.Count);

        var rows = File.ReadAllLines(Path.Combine(f.Registry, "ledger", "designc.jsonl")).Select(l => JsonDocument.Parse(l).RootElement).Where(r => r.GetProperty("kind").GetString() == "ui_preview_rendered").ToList();
        var row = Assert.Single(rows);
        var hashed = row.GetProperty("files").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        Assert.Equal(files.Order(StringComparer.Ordinal), hashed.Keys.Order(StringComparer.Ordinal));
        foreach (var (rel, hash) in hashed) Assert.Equal(hash, Factory.Harness.Registry.UiPreview.Sha256Hex(File.ReadAllBytes(Path.Combine(dir, rel))));
        Assert.Equal(Factory.Harness.Registry.UiPreview.Sha256Hex(File.ReadAllBytes(Path.Combine(f.Registry, "ui", "specs", "bordereaux.json"))), row.GetProperty("spec_hash").GetString());
        Assert.StartsWith("sha256:", row.GetProperty("image").GetString());

        // The person's checkout was never written, and the scratch worktree is gone.
        Assert.Empty(await new Factory.S2.Git.GitWorkspace(f.Repo, Path.Combine(f.Repo, ".factory")).ChangedPathsAsync(f.Repo, default));
        Assert.DoesNotContain("s2-preview-", (await Factory.Harness.ProcessRunner.RunAsync("git", ["-C", f.Repo, "worktree", "list"], f.Repo, TimeSpan.FromSeconds(30))).Output);
    }

    [Fact]
    public async Task A_component_the_app_does_not_export_never_renders_so_the_run_is_could_not_run_and_writes_no_row()
    {
        if (!Enabled || UiPreviewFixture.DesigncDir() is not { } designc) return;
        var f = await UiPreviewFixture.CreateAsync();
        var registryTs = Path.Combine(f.Repo, "ui-contract", "registry.ts");
        File.WriteAllText(registryTs, string.Join('\n', File.ReadAllLines(registryTs).Where(l => !l.Contains("MetricCard"))) + "\n");
        await UiPreviewFixture.Git(f.Repo, "commit", "-aqm", "drop MetricCard", "--author=t <t@t>");

        var result = await Runner().RunAsync(Request(f, designc), default);

        Assert.Equal(3, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(f.Registry, "ledger", "designc.jsonl")) && File.ReadAllText(Path.Combine(f.Registry, "ledger", "designc.jsonl")).Contains("ui_preview_rendered"));
        Assert.False(Directory.Exists(Path.Combine(f.Registry, "ui", "previews")));
    }
}
