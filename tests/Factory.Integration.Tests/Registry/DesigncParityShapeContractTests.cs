using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Microsoft.FSharp.Core;
using Xunit;

namespace Factory.Integration.Tests.Registry;

/// The Factory's port of designc's Parity.shape / sameShape / jsonShape (Core/RequestParity.fs) against designc's contract files
/// (docs/parity-cases.json, docs/parity-requests.example.json; Fixtures/designc holds copies, pinned in contracts.lock.json).
/// If one fails, designc and the Factory disagree on what a request shape is: change both sides and the fixtures together.
public class DesigncParityShapeContractTests
{
    private static string Fx(params string[] p) => Path.Combine([AppContext.BaseDirectory, "Fixtures", .. p]);
    private static JsonElement Json(string path) => JsonDocument.Parse(File.ReadAllText(path)).RootElement;

    [Fact]
    public void Every_shape_case_of_designc_gives_the_same_answer()
    {
        var cases = Json(Fx("designc", "parity-cases.json")).GetProperty("shape").EnumerateArray().ToList();
        Assert.NotEmpty(cases);
        foreach (var c in cases)
        {
            var a = Shape.shape(c.GetProperty("a").GetString()!);
            var b = Shape.shape(c.GetProperty("b").GetString()!);
            if (c.TryGetProperty("shapeA", out var sa)) Assert.Equal(sa.GetString(), a);
            if (c.TryGetProperty("shapeB", out var sb)) Assert.Equal(sb.GetString(), b);
            Assert.True(c.GetProperty("same").GetBoolean() == Shape.sameShape(a, b), c.GetProperty("id").GetString());
        }
    }

    [Fact]
    public void The_synthetic_har_shapes_to_the_rows_designc_published()
    {
        var rows = Json(Fx("designc", "parity-requests.example.json")).EnumerateArray().ToList();
        var har = Json(Fx("har", "bordereaux.har")).GetProperty("log").GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(3, har.Count);
        for (var i = 0; i < har.Count; i++)
        {
            var text = har[i].GetProperty("response").GetProperty("content").GetProperty("text").GetString();
            Assert.NotNull(text);
            var got = JsonShapes.toJson(JsonShapes.ofBody(FSharpOption<string>.Some(text)).Value);
            var want = JsonShapes.toJson(JsonShapes.ofShapeElement(rows[i].GetProperty("response")));
            Assert.Equal(want, got);
        }
    }

    /// With node and a designc checkout beside this repo: the same strings through designc's own parity.js.
    [Fact]
    public async Task The_port_agrees_with_parity_js_on_a_table_of_values()
    {
        var designc = Verify.UiPreviewFixture.DesigncDir();
        if (designc is null) return;
        string[] values = ["", "15/01/2026", "1,2", "(500.00)", "2026-Q3", "9531350", "$2,140,000", "2026-10-04T10:00:00Z", "McDonald", "7 lines missing location", "B-2", "x y", "12:30:45", "0123", "TV 4K"];
        var script = "const P=require(process.argv[1]);console.log(JSON.stringify(JSON.parse(process.argv[2]).map(x=>P.shape(x))))";
        var run = await ProcessRunner.RunAsync("node", ["-e", script, Path.Combine(designc, "renderer", "parity.js"), JsonSerializer.Serialize(values)], designc, TimeSpan.FromSeconds(30));
        if (!run.Succeeded) return; // no node here
        var want = JsonSerializer.Deserialize<string[]>(run.Output.Trim())!;
        Assert.Equal(want, values.Select(Shape.shape).ToArray());
    }
}
