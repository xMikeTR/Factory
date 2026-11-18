using Factory.Core;
using Factory.Harness.Registry;
using Factory.Integration.Tests.Support;
using Factory.S2;
using Xunit;

namespace Factory.Integration.Tests.Workflow;

using Core = Factory.Core.Core;

public class HandoverInputTests : IDisposable
{
    private readonly string _state = Directory.CreateTempSubdirectory("state-").FullName;
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "handover");

    [Fact]
    public async Task A_Designc_directory_is_frozen_to_json_in_the_state_dir()
    {
        var path = await HandoverInput.ResolveAsync(Fixture, _state, default);

        Assert.StartsWith(_state, path);
        var expected = await new DesigncHandoverReader().LoadAsync(Fixture, S2Config.Default, default);
        Assert.Equal(expected, Core.ParseHandover(await File.ReadAllTextAsync(path)));
    }

    [Fact]
    public async Task A_json_file_is_used_as_is()
    {
        var json = Path.Combine(_state, "h.json");
        await File.WriteAllTextAsync(json, "{}");
        Assert.Equal(json, await HandoverInput.ResolveAsync(json, _state, default));
    }

    [Fact]
    public async Task A_handover_with_UI_screens_is_frozen_with_its_previews_beside_the_json_and_the_run_gets_a_copy()
    {
        var h = UiHandoverDir.Create();
        var json = await HandoverInput.ResolveAsync(h.Dir, _state, default);
        var folder = HandoverInput.PreviewsFolder(json);
        Assert.Equal(40, Directory.EnumerateFiles(Path.Combine(folder, "bordereaux"), "*", SearchOption.AllDirectories).Count());
        Assert.Equal(File.ReadAllBytes(h.Preview("ready-chromium-linux.png")), File.ReadAllBytes(Path.Combine(folder, "bordereaux", "ready-chromium-linux.png")));

        var run = Path.Combine(_state, "runs", "r1");
        HandoverInput.CopyUiPreviewsToRun(Core.ParseHandover(await File.ReadAllTextAsync(json)), json, run);
        Assert.True(File.Exists(Path.Combine(run, "ui", "previews", "bordereaux", "placements", "ready__hdr-chromium-linux.png")));
    }

    [Fact]
    public async Task A_touched_picture_is_refused_and_nothing_is_frozen_beside_it()
    {
        var h = UiHandoverDir.Create();
        File.AppendAllText(h.Preview("ready-chromium-linux.png"), "x");
        var e = await Assert.ThrowsAsync<FactoryRejectedException>(() => HandoverInput.ResolveAsync(h.Dir, _state, default));
        Assert.Contains(e.Errors, x => x.Contains("ready-chromium-linux.png") && x.Contains("sha256"));
        Assert.False(Directory.Exists(Path.Combine(_state, "handovers", Path.GetFileName(h.Dir) + ".ui")));
    }

    [Fact]
    public async Task A_missing_or_extra_picture_is_refused()
    {
        var missing = UiHandoverDir.Create();
        File.Delete(missing.Preview("ready.aria.yml"));
        var e1 = await Assert.ThrowsAsync<FactoryRejectedException>(() => HandoverInput.ResolveAsync(missing.Dir, _state, default));
        Assert.Contains(e1.Errors, x => x.Contains("ready.aria.yml") && x.Contains("missing"));

        var extra = UiHandoverDir.Create();
        File.WriteAllText(extra.Preview("placements/ready__new-chromium-linux.png"), "x");
        var e2 = await Assert.ThrowsAsync<FactoryRejectedException>(() => HandoverInput.ResolveAsync(extra.Dir, _state, default));
        Assert.Contains(e2.Errors, x => x.Contains("ready__new-chromium-linux.png") && x.Contains("not named"));
    }

    [Fact]
    public async Task A_handover_json_with_screens_and_no_previews_beside_it_is_refused_at_start()
    {
        var h = UiHandoverDir.Create();
        var json = await HandoverInput.ResolveAsync(h.Dir, _state, default);
        Directory.Delete(Path.GetDirectoryName(HandoverInput.PreviewsFolder(json))!, recursive: true);
        Assert.Throws<FactoryRejectedException>(() =>
            HandoverInput.CopyUiPreviewsToRun(Core.ParseHandover(File.ReadAllText(json)), json, Path.Combine(_state, "runs", "r2")));
    }

    public void Dispose() => Directory.Delete(_state, recursive: true);
}
