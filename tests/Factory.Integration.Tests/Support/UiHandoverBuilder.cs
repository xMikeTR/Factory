using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Factory.Harness.Registry;

namespace Factory.Integration.Tests.Support;

/// A frozen designc handover directory for the fixture screen (bordereaux): handover.yaml, elements.json, ui.json, the spec, template
/// and components from Fixtures/ui-preview-app/registry, and the approved pictures under ui/previews/<screen>/. Every hash is honest
/// unless a test changes it afterwards.
public sealed record UiHandoverDir(string Dir, string Screen, string UiJsonPath)
{
    public const string Screen0 = "bordereaux";

    public static string Registry => Path.Combine(AppContext.BaseDirectory, "Fixtures", "ui-preview-app", "registry");

    /// `previews`: path under ui/previews/<screen>/ to bytes; null writes a stand-in file for every name the spec expects.
    /// `holdout`: the sample's sha256 and a hidden case, listed by hash only (never copied).
    public static UiHandoverDir Create(IReadOnlyDictionary<string, byte[]>? previews = null, string workItem = "W-ui", Func<string, string>? spec = null, bool holdout = true, string? holdoutSha = null, IReadOnlyList<(string Criterion, int Case, string State, string Sha)>? holdoutCases = null)
    {
        var dir = Directory.CreateTempSubdirectory("ui-handover").FullName;
        var files = new Dictionary<string, string>();
        void Text(string rel, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, rel))!);
            File.WriteAllText(Path.Combine(dir, rel), text);
            files[rel] = text;
        }
        var specText = File.ReadAllText(Path.Combine(Registry, "ui", "specs", Screen0 + ".json"));
        if (spec is not null) specText = spec(specText);
        var templatePath = "ui/pack/templates/list-detail.json";
        var template = File.ReadAllText(Path.Combine(Registry, templatePath));
        var components = Directory.EnumerateFiles(Path.Combine(Registry, "ui", "pack", "components"), "*.json").Order(StringComparer.Ordinal)
            .Select(f => (Name: Path.GetFileNameWithoutExtension(f), Path: "ui/pack/components/" + Path.GetFileName(f), Text: File.ReadAllText(f))).ToList();

        Text("ui/specs/bordereaux.json", specText);
        Text(templatePath, template);
        foreach (var c in components) Text(c.Path, c.Text);

        var pics = previews ?? UiPreview.ExpectedFiles(specText).ToDictionary(f => f, f => Encoding.UTF8.GetBytes("picture " + f));
        foreach (var (rel, bytes) in pics)
        {
            var full = Path.Combine(dir, "ui", "previews", Screen0, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
        }

        var holdoutFile = Path.Combine(Registry, "ui", "holdout", Screen0 + ".json");
        var ui = new JsonObject
        {
            ["schema"] = "designc.handover.ui/1",
            ["pack"] = new JsonObject { ["name"] = "acme-ui", ["hash"] = "pinhash0123456789" },
            ["screens"] = new JsonArray(new JsonObject
            {
                ["screen"] = Screen0,
                ["spec"] = new JsonObject { ["path"] = "ui/specs/bordereaux.json", ["sha256"] = DesigncHandoverReader.Sha256(specText) },
                ["template"] = new JsonObject { ["path"] = templatePath, ["sha256"] = DesigncHandoverReader.Sha256(template) },
                ["components"] = new JsonArray(components.Select(c => (JsonNode)new JsonObject
                    { ["name"] = c.Name, ["path"] = c.Path, ["sha256"] = DesigncHandoverReader.Sha256(c.Text) }).ToArray()),
                ["previews"] = new JsonObject
                {
                    ["approved_ts"] = 1700,
                    ["files"] = new JsonObject(pics.OrderBy(p => p.Key, StringComparer.Ordinal)
                        .Select(p => KeyValuePair.Create(p.Key, (JsonNode?)Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(p.Value))))),
                },
                ["holdout"] = holdout
                    ? new JsonObject { ["path"] = "ui/holdout/bordereaux.json", ["sha256"] = holdoutSha ?? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(holdoutFile))) }
                    : null,
                ["holdout_cases"] = new JsonArray((holdoutCases ?? []).Select(c => (JsonNode)new JsonObject { ["criterion"] = c.Criterion, ["case"] = c.Case, ["state"] = c.State, ["sha256"] = c.Sha }).ToArray()),
            }),
        };
        var uiText = ui.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        Text("ui.json", uiText);
        Text("elements.json", "{\"schema\":\"designc.handover.elements/1\",\"elements\":[]}");

        var yaml = new StringBuilder($"id: 20261115-uitest\nelements: \"elements.json\"\nui: \"ui.json\"\nui_screens: [\"{Screen0}\"]\n");
        if (workItem.Length > 0) yaml.Append($"work_item: \"{workItem}\"\n");
        yaml.Append("files:\n");
        foreach (var (rel, text) in files) yaml.Append($"  - {{ path: \"{rel}\", hash: \"{DesigncHandoverReader.Hash16(text)}\" }}\n");
        File.WriteAllText(Path.Combine(dir, "handover.yaml"), yaml.ToString());
        return new(dir, Screen0, Path.Combine(dir, "ui.json"));
    }

    /// Rewrite one frozen text file AND its hash16 in handover.yaml, so the integrity loop passes and only the ui.json sha256 check can object.
    public void ReplaceText(string rel, string text)
    {
        var old = File.ReadAllText(Path.Combine(Dir, rel));
        File.WriteAllText(Path.Combine(Dir, rel), text);
        var manifest = Path.Combine(Dir, "handover.yaml");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace(DesigncHandoverReader.Hash16(old), DesigncHandoverReader.Hash16(text)));
    }

    public string Preview(string rel) => Path.Combine(Dir, "ui", "previews", Screen, rel);
}
