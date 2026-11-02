using System.Text.Json.Nodes;
using Factory.Harness.Registry;
using Xunit;

namespace Factory.Integration.Tests.Registry;

/// designc owns the context bundle (docs/context-bundle.schema.json); Fixtures/designc holds a copy of its
/// docs/context-bundle.example.json, which designc pins to real output. Any failure to get one is "no bundle".
public class ContextSourceTests
{
    private static readonly string Example =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "designc", "context-bundle.example.json"));

    private static BundleRequest Request(string role = "builder", string? handover = "20261102-abcd1234") =>
        new(handover, "W-tags", "T-1", role, ["Tags/Tags API", "Tags/Tags UI"]);

    private static string Edit(Action<JsonObject> change)
    {
        var o = JsonNode.Parse(Example)!.AsObject();
        change(o);
        return o.ToJsonString();
    }

    [Fact]
    public void Designcs_example_parses_with_its_id_key_and_procedures_and_keeps_the_text()
    {
        var fetched = Assert.IsType<BundleFetch.Fetched>(ContextBundle.Parse(Example, Request(), "file"));
        var b = fetched.Bundle;

        Assert.Equal("sha256:1956c88a2f776703359ec9f6b4eb920a6cc9b0df775270fcdcf8e4871c26be8d", b.BundleId);
        Assert.Equal("builder", b.Role);
        Assert.Equal("20261102-abcd1234", b.Handover);
        Assert.Equal(["Tags/Tags API", "Tags/Tags UI"], b.Elements);
        var p = Assert.Single(b.Procedures);
        Assert.Equal(("change-ui-list", "2", "30fead3d350b4809", "Change a list screen in the Tags UI"), (p.Id, p.Version, p.Hash, p.Title));
        Assert.Equal(3, p.Steps.Count);
        Assert.Equal(Example, b.Json);
    }

    [Fact]
    public void A_request_without_a_handover_accepts_the_bundles_handover()
    {
        Assert.IsType<BundleFetch.Fetched>(ContextBundle.Parse(Example, Request(handover: null), "file"));
    }

    public static TheoryData<string, string> Broken() => new()
    {
        { "{ not json", "not JSON" },
        { "[]", "not a JSON object" },
        { Edit(o => o["schema"] = "designc.context.bundle/2"), "schema" },
        { Edit(o => o.Remove("schema")), "schema" },
        { Edit(o => o["bundle_id"] = "sha256:abc"), "bundle_id" },
        { Edit(o => o.Remove("bundle_id")), "bundle_id" },
        { Edit(o => o.Remove("key")), "key" },
        { Edit(o => o["key"]!["role"] = "reviewer"), "role" },
        { Edit(o => o["key"]!["handover"] = "20261102-other"), "handover" },
        { Edit(o => o["key"]!["elements"] = "Tags/Tags API"), "elements" },
        { Edit(o => o["procedures"]![0]!["hash"] = "XYZ"), "procedure" },
        { Edit(o => o["procedures"]![0]!.AsObject().Remove("id")), "procedure" },
        { Edit(o => o["procedures"] = new JsonObject()), "procedures" },
    };

    [Theory]
    [MemberData(nameof(Broken))]
    public void A_bundle_that_is_not_designcs_shape_or_not_for_this_request_is_missed_with_a_reason(string json, string reasonMentions)
    {
        var missed = Assert.IsType<BundleFetch.Missed>(ContextBundle.Parse(json, Request(), "file"));
        Assert.Equal("file", missed.Source);
        Assert.Contains(reasonMentions, missed.Reason);
    }
}
