using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Harness.Registry;
using Factory.Integration.Tests.Support;
using Xunit;

namespace Factory.Integration.Tests.Registry;

/// The reader's half of designc's `designc.handover.ui/1` (F2): ui.json becomes Handover.Ui, and what it cannot trust it refuses.
public class UiHandoverReaderTests
{
    private static Task<Handover> Load(string dir) => new DesigncHandoverReader().LoadAsync(dir, S2Config.Default, CancellationToken.None);

    [Fact]
    public async Task ui_json_becomes_the_screens_with_texts_previews_and_holdout_hashes()
    {
        var h = UiHandoverDir.Create(holdoutSha: new string('a', 64));
        var ui = (await Load(h.Dir)).Ui.Single();
        Assert.Equal(("bordereaux", "BordereauxList", "src/BordereauxList.tsx"), (ui.Screen, ui.Component, ui.File));
        Assert.Equal("ui/specs/bordereaux.json", ui.Spec.Path);
        Assert.Contains("\"component\": \"BordereauxList\"", ui.Spec.Text);
        Assert.Equal(6, ui.Components.Length);
        Assert.Equal(40, ui.Previews.Length);
        Assert.Equal(new string('a', 64), ui.Holdout.Value);
    }

    [Fact]
    public async Task A_handover_without_ui_has_no_screens()
    {
        var dir = Path.Combine(Path.GetTempPath(), "s2-handover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        const string elements = "{\"schema\":\"designc.handover.elements/1\",\"elements\":[]}";
        await File.WriteAllTextAsync(Path.Combine(dir, "elements.json"), elements);
        await File.WriteAllTextAsync(Path.Combine(dir, "handover.yaml"),
            $"id: 20261115-none\nelements: \"elements.json\"\nfiles:\n  - {{ path: \"elements.json\", hash: \"{DesigncHandoverReader.Hash16(elements)}\" }}\n");
        Assert.Empty((await Load(dir)).Ui);
    }

    /// designc's own example (docs/handover-ui.example.json, copied by selfcheck --update) is the contract: the reader must take its shape.
    [Fact]
    public async Task The_contract_example_is_readable()
    {
        var example = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "designc", "handover-ui.json")))!;
        var dir = Directory.CreateTempSubdirectory("ui-contract").FullName;
        var files = new Dictionary<string, string>();
        void Stub(JsonNode f, string text)
        {
            var path = f["path"]!.GetValue<string>();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, path))!);
            File.WriteAllText(Path.Combine(dir, path), text);
            files[path] = text;
            f["sha256"] = DesigncHandoverReader.Sha256(text);
        }
        foreach (var s in example["screens"]!.AsArray())
        {
            Stub(s!["spec"]!, "{\"component\":\"BordereauxList\",\"file\":\"src/BordereauxList.tsx\"}");
            Stub(s["template"]!, "{}");
            foreach (var c in s["components"]!.AsArray()) Stub(c!, "{}");
        }
        var uiText = example.ToJsonString();
        File.WriteAllText(Path.Combine(dir, "ui.json"), uiText); files["ui.json"] = uiText;
        const string elements = "{\"schema\":\"designc.handover.elements/1\",\"elements\":[]}";
        File.WriteAllText(Path.Combine(dir, "elements.json"), elements); files["elements.json"] = elements;
        var yaml = "id: 20261115-contract\nelements: \"elements.json\"\nui: \"ui.json\"\nui_screens: [\"bordereaux\"]\nfiles:\n"
            + string.Concat(files.Select(f => $"  - {{ path: \"{f.Key}\", hash: \"{DesigncHandoverReader.Hash16(f.Value)}\" }}\n"));
        File.WriteAllText(Path.Combine(dir, "handover.yaml"), yaml);

        var ui = (await Load(dir)).Ui.Single();
        Assert.Equal(3, ui.Previews.Length);
        Assert.Equal("AC-UI-1", ui.HoldoutCases.Single().Criterion);
        Assert.Equal("ready", ui.HoldoutCases.Single().State);
        Assert.Equal(6, ui.Components.Length);
    }

    [Fact]
    public async Task A_text_copy_whose_sha256_differs_from_ui_json_is_refused()
    {
        var h = UiHandoverDir.Create();
        h.ReplaceText("ui/pack/components/Button.json", "{\"name\":\"Button\"}"); // hash16 updated, ui.json's sha256 not
        var e = await Assert.ThrowsAsync<FactoryRejectedException>(() => Load(h.Dir));
        Assert.Contains(e.Errors, x => x.Contains("ui/pack/components/Button.json") && x.Contains("sha256"));
    }

    [Fact]
    public async Task A_spec_without_a_file_is_refused_because_the_page_test_is_always_generated()
    {
        var h = UiHandoverDir.Create(spec: t => t.Replace("  \"file\": \"src/BordereauxList.tsx\",\n", ""));
        var e = await Assert.ThrowsAsync<FactoryRejectedException>(() => Load(h.Dir));
        Assert.Contains(e.Errors, x => x.Contains("has no file, so the page test cannot be generated"));
    }

    [Fact]
    public async Task A_screen_missing_from_ui_screens_is_refused()
    {
        var h = UiHandoverDir.Create();
        var manifest = Path.Combine(h.Dir, "handover.yaml");
        await File.WriteAllTextAsync(manifest, (await File.ReadAllTextAsync(manifest)).Replace("ui_screens: [\"bordereaux\"]", "ui_screens: []"));
        var e = await Assert.ThrowsAsync<FactoryRejectedException>(() => Load(h.Dir));
        Assert.Contains(e.Errors, x => x.Contains("not in handover.yaml ui_screens"));
    }
}
