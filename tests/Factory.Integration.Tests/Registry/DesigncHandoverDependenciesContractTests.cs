using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Harness.Registry;
using Xunit;

namespace Factory.Integration.Tests.Registry;

/// The reader's half of designc's `designc.handover.dependencies/1` (C3): dependencies.json becomes Handover.Dependencies,
/// checked against the code-backed elements of elements.json.
public class DesigncHandoverDependenciesContractTests
{
    private static string Fx(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "designc", name);

    private static Task<Handover> Load(string dir) => new DesigncHandoverReader().LoadAsync(dir, S2Config.Default, CancellationToken.None);

    private static JsonObject Element(string id, string code, string? repo = null)
    {
        var e = new JsonObject { ["id"] = id, ["name"] = id, ["kind"] = "component", ["code"] = new JsonArray(code), ["risk"] = "low" };
        if (repo is not null) e["repo"] = repo;
        return e;
    }

    /// A handover dir holding the four coded elements of the example and the pinned example as dependencies.json.
    public static string Dir(Action<JsonNode>? editDeps = null, Action<JsonArray>? editElements = null, bool withKey = true, bool freeze = true)
    {
        var deps = JsonNode.Parse(File.ReadAllText(Fx("handover-dependencies.json")))!;
        editDeps?.Invoke(deps);
        var elements = new JsonArray(Element("S/Api/Ctl", "api/ctl/**"), Element("S/Api/Repo", "api/repo/**"), Element("S/Db", "db/**"), Element("S/Web", "web/**", "Ui"));
        editElements?.Invoke(elements);
        var elementsJson = new JsonObject { ["schema"] = "designc.handover.elements/1", ["elements"] = elements }.ToJsonString();
        var depsJson = deps.ToJsonString();
        var dir = Directory.CreateTempSubdirectory("deps-handover").FullName;
        File.WriteAllText(Path.Combine(dir, "elements.json"), elementsJson);
        File.WriteAllText(Path.Combine(dir, "dependencies.json"), depsJson);
        var yaml = "id: 20261120-dep\nelements: \"elements.json\"\n" + (withKey ? "dependencies: \"dependencies.json\"\n" : "") + "files:\n"
            + $"  - {{ path: \"elements.json\", hash: \"{DesigncHandoverReader.Hash16(elementsJson)}\" }}\n"
            + (freeze ? $"  - {{ path: \"dependencies.json\", hash: \"{DesigncHandoverReader.Hash16(depsJson)}\" }}\n" : "");
        File.WriteAllText(Path.Combine(dir, "handover.yaml"), yaml);
        return dir;
    }

    private static async Task<string> Refused(string dir) => (await Assert.ThrowsAsync<FactoryRejectedException>(() => Load(dir))).Message;

    [Fact]
    public void The_pinned_schema_and_example_agree_on_the_schema_id()
    {
        var schema = JsonNode.Parse(File.ReadAllText(Fx("handover-dependencies.schema.json")))!;
        Assert.Equal("designc.handover.dependencies/1", JsonNode.Parse(File.ReadAllText(Fx("handover-dependencies.json")))!["schema"]!.GetValue<string>());
        Assert.Equal("designc.handover.dependencies/1", schema["$id"]!.GetValue<string>());
        Assert.Equal("designc.handover.dependencies/1", schema["properties"]!["schema"]!["const"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_contract_example_is_readable()
    {
        var deps = (await Load(Dir())).Dependencies.Value;
        Assert.Equal(4, deps.Length);
        string[] Of(string id) => deps.Single(d => d.Element.Value == id).Allows.Select(a => a.Value).ToArray();
        Assert.Equal(["S/Api/Ctl", "S/Api/Repo"], Of("S/Web"));
        Assert.Equal(["S/Api/Repo"], Of("S/Api/Ctl"));
        Assert.Equal(["S/Db"], Of("S/Api/Repo"));
        Assert.Empty(Of("S/Db"));
    }

    [Fact]
    public async Task Without_the_key_there_is_no_allow_list() => Assert.Null((await Load(Dir(withKey: false))).Dependencies);

    [Fact]
    public async Task Another_schema_is_refused() =>
        Assert.Contains("designc.handover.dependencies/1", await Refused(Dir(d => d["schema"] = "designc.handover.dependencies/2")));

    [Fact]
    public async Task An_unknown_element_is_refused() =>
        Assert.Contains("dependencies.json: 'S/Nope'", await Refused(Dir(d => d["elements"]![0]!["element"] = "S/Nope")));

    [Fact]
    public async Task A_code_mismatch_is_refused() =>
        Assert.Contains("dependencies.json: 'S/Db' code", await Refused(Dir(d => d["elements"]![2]!["code"] = new JsonArray("other/**"))));

    [Fact]
    public async Task A_repo_mismatch_is_refused() =>
        Assert.Contains("dependencies.json: 'S/Web' repo", await Refused(Dir(d => d["elements"]![3]!["repo"] = "Api")));

    [Fact]
    public async Task A_missing_repo_where_the_element_has_one_is_refused() =>
        Assert.Contains("dependencies.json: 'S/Web' repo", await Refused(Dir(d => ((JsonObject)d["elements"]![3]!).Remove("repo"))));

    [Fact]
    public async Task An_unknown_allows_id_is_refused() =>
        Assert.Contains("dependencies.json: 'S/Web' allows 'S/Ghost'", await Refused(Dir(d => d["elements"]![3]!["allows"]!.AsArray().Add("S/Ghost"))));

    [Fact]
    public async Task A_self_allow_is_refused() =>
        Assert.Contains("dependencies.json: 'S/Db' allows itself", await Refused(Dir(d => d["elements"]![2]!["allows"]!.AsArray().Add("S/Db"))));

    [Fact]
    public async Task A_duplicate_entry_is_refused() =>
        Assert.Contains("dependencies.json: 'S/Db' is listed twice", await Refused(Dir(d => d["elements"]![3]!["element"] = "S/Db")));

    [Fact]
    public async Task A_code_backed_element_that_is_missing_is_refused() =>
        Assert.Contains("dependencies.json: 'S/Web' is not listed", await Refused(Dir(d => d["elements"]!.AsArray().RemoveAt(3))));

    [Fact]
    public async Task A_dependencies_file_that_is_not_frozen_is_refused() =>
        Assert.Contains("dependencies.json", await Refused(Dir(freeze: false)));
}
