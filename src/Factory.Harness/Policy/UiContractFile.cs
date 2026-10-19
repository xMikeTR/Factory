using Factory.Core;
using YamlDotNet.Serialization;

namespace Factory.Harness.Policy;

/// The UI features a human says must keep working: `.factory/ui-contract.yaml`. Protected like the policy (`.factory/**`),
/// read from the base checkout, never a task worktree. A missing file is an empty contract.
///
///   testIds: [export-btn]
///   routes: [/reports]
///   uiTests: ["exports a report"]
public static class UiContractFile
{
    public const string RelativePath = ".factory/ui-contract.yaml";

    private sealed class Doc { public List<string>? TestIds { get; set; } public List<string>? Routes { get; set; } public List<string>? UiTests { get; set; } }

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static IReadOnlyList<UiItem> Load(string repoPath)
    {
        var path = Path.Combine(repoPath, RelativePath);
        if (!File.Exists(path)) return [];
        var doc = PolicyYaml.Read<Doc>(Yaml, path) ?? new Doc();
        var errors = new List<string>();
        List<UiItem> Items(string section, List<string>? ids, UiItemKind kind)
        {
            if (ids?.Any(string.IsNullOrWhiteSpace) == true) errors.Add($"{section}: entries must not be blank");
            return (ids ?? []).Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => new UiItem(kind, i.Trim())).ToList();
        }
        var items = new[] { Items("testIds", doc.TestIds, UiItemKind.TestId), Items("routes", doc.Routes, UiItemKind.Route), Items("uiTests", doc.UiTests, UiItemKind.UiTest) };
        if (errors.Count > 0) throw new FactoryRejectedException($"{path} is invalid", errors.ToArray());
        return items.SelectMany(i => i).ToList();
    }
}
