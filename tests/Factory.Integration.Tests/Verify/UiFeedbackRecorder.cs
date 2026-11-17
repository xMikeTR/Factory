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

/// Re-records the F7 fixtures (Fixtures/ui-feedback/): the real terminal output, JSON report, screenshot folder and HTML report
/// folder of a failing page run in the runner image, for an ARIA-breaking page and for the kit's WrongTone. Opt-in: needs
/// FACTORY_TEST_DOCKER=1 and FACTORY_RECORD_UI_FEEDBACK=<target dir>; does nothing otherwise.
[Trait("needs", "docker")]
public sealed class UiFeedbackRecorder
{
    private static readonly string? Target = Environment.GetEnvironmentVariable("FACTORY_RECORD_UI_FEEDBACK");

    private static async Task RecordAsync(string name, Action<string> sabotage, string outDir)
    {
        var state = Directory.CreateTempSubdirectory("ui-record").FullName;
        var fx = await UiPreviewFixture.CreateAsync();
        var render = await new UiPreviewRunner(new DockerSandboxFactory(new DockerSandboxOptions()))
            .RunAsync(new UiPreviewRequest(fx.Registry, "bordereaux", fx.Repo, UiPreviewFixture.DesigncDir()!, "factory-s2-runner:latest"), default);
        Assert.True(render.ExitCode == 0, render.Message);
        var pdir = Path.Combine(fx.Registry, "ui", "previews", "bordereaux");
        var previews = Directory.EnumerateFiles(pdir, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(pdir, p).Replace('\\', '/'), File.ReadAllBytes);
        var h = UiHandoverDir.Create(previews);
        var json = await HandoverInput.ResolveAsync(h.Dir, state, default);
        var handover = Core.ParseHandover(await File.ReadAllTextAsync(json));
        var runDir = Path.Combine(state, "runs", "r1");
        HandoverInput.CopyUiPreviewsToRun(handover, json, runDir);
        var git = new GitWorkspace(fx.Repo, Path.Combine(state, "ws"));
        var integration = await git.EnsureIntegrationAsync("r1", "main", default);
        await UiContractSeed.SeedAsync(git, "r1", integration, handover, runDir, default);
        sabotage(integration);

        await using var sandbox = await new DockerSandboxFactory(new DockerSandboxOptions()).ForTeamAsync("integration", integration, default);
        var run = await sandbox.RunAsync(UiPageCheck.DefaultCommand, integration, TimeSpan.FromMinutes(10),
            new Dictionary<string, string> { ["CHROME_PATH"] = UiPreviewRunner.ChromePath, ["CI"] = "1" }, default);
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, name + ".output.txt"), run.Output);
        File.WriteAllText(Path.Combine(outDir, name + ".exit.txt"), run.ExitCode.ToString());
        var dst = Path.Combine(outDir, name + ".tree");
        foreach (var rel in new[] { ".vitest", "ui-contract/__screenshots__", "__screenshots__", "ui-contract/tests/__screenshots__", "ui-contract/previews" })
        {
            var src = Path.Combine(integration, rel);
            if (!Directory.Exists(src)) continue;
            foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                var r = Path.GetRelativePath(integration, f);
                if (rel == "ui-contract/previews" && !r.Contains("actual") && !r.Contains("diff")) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dst, r))!);
                File.Copy(f, Path.Combine(dst, r), true);
            }
        }
    }

    [Fact]
    public async Task Record()
    {
        if (Environment.GetEnvironmentVariable("FACTORY_TEST_DOCKER") != "1" || string.IsNullOrEmpty(Target) || UiPreviewFixture.DesigncDir() is null) return;
        await RecordAsync("aria", i =>
        {
            var p = Path.Combine(i, "src", "BordereauxList.tsx");
            File.WriteAllText(p, File.ReadAllText(p).Replace("title=\"Bordereaux\"", "title=\"Bordereaux list\""));
        }, Target);
        await RecordAsync("pixels", i =>
            File.Copy(Path.Combine(UiPreviewFixture.FixtureRoot, "WrongTone", "BordereauxList.tsx"), Path.Combine(i, "src", "BordereauxList.tsx"), true), Target);
    }
}
