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

        Assert.Equal("sha256:4a57987a80e976e2ab1ec2ca7b18f908b2d0c01778161568fe045f4f9a5df3ec", b.BundleId);
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

    [Fact]
    public void A_tampered_field_makes_the_bundle_id_not_match_and_the_bundle_is_missed()
    {
        var tampered = Edit(o => o["procedures"]![0]!["steps"]![0] = "do something else");
        var missed = Assert.IsType<BundleFetch.Missed>(ContextBundle.Parse(tampered, Request(), "file"));
        Assert.Contains("bundle_id does not match", missed.Reason);
    }

    [Fact]
    public void Key_order_and_whitespace_do_not_change_the_computed_id()
    {
        var o = JsonNode.Parse(Example)!.AsObject();
        var reordered = new JsonObject();
        foreach (var kv in o.Reverse()) reordered[kv.Key] = kv.Value?.DeepClone();
        Assert.IsType<BundleFetch.Fetched>(ContextBundle.Parse(reordered.ToJsonString(new() { WriteIndented = true }), Request(), "file"));
    }

    // Expected value derived from designc's own canonical(): sha256 of the JS JSON.stringify form of this object.
    [Fact]
    public void Canonical_form_matches_JS_for_escapes_and_non_ascii()
    {
        var json = "{\"b\":[1,true,null,\"<&>'\\u00e9\\u2028\\n\\u0001\"],\"a\":{\"z\":1.5,\"y\":\"q\\\"\\\\\"}}";
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("sha256:" + JsExpected, ContextBundle.ComputeId(doc.RootElement));
    }

    private static readonly string JsExpected = "41f85b6edd5d538f5988efed532feb4e7047b5d7403e1af2371acd4062f3b490";

    // ---- Step 2: the stub ----

    [Fact]
    public async Task No_source_returns_no_bundle()
    {
        var missed = Assert.IsType<BundleFetch.Missed>(await new NoContextSource().FetchAsync(Request(), default));
        Assert.Equal("none", missed.Source);
    }

    // ---- Step 3: the file in the handover folder ----

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "ctx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public async Task The_file_source_reads_bundles_role_json_in_the_handover_folder()
    {
        var dir = TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "bundles"));
        File.WriteAllText(Path.Combine(dir, "bundles", "builder.json"), Example);

        var fetched = Assert.IsType<BundleFetch.Fetched>(await new FileContextSource(dir).FetchAsync(Request(), default));
        Assert.StartsWith("sha256:4a57987a", fetched.Bundle.BundleId);
    }

    [Fact]
    public async Task The_file_source_misses_when_the_file_is_absent_for_the_role_or_is_not_a_bundle()
    {
        var dir = TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "bundles"));
        File.WriteAllText(Path.Combine(dir, "bundles", "builder.json"), Example);
        File.WriteAllText(Path.Combine(dir, "bundles", "reviewer.json"), "{ nope");
        var source = new FileContextSource(dir);

        Assert.IsType<BundleFetch.Missed>(await source.FetchAsync(Request("tester"), default));          // no tester.json
        var bad = Assert.IsType<BundleFetch.Missed>(await source.FetchAsync(Request("reviewer"), default)); // not JSON
        Assert.Equal("file", bad.Source);
        Assert.IsType<BundleFetch.Missed>(await new FileContextSource(Path.Combine(dir, "nowhere")).FetchAsync(Request(), default));
    }

    [Theory]
    [InlineData("../builder")]
    [InlineData("a/b")]
    public async Task The_file_source_never_reads_outside_bundles_whatever_the_role(string role)
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "builder.json"), Example);
        Assert.IsType<BundleFetch.Missed>(await new FileContextSource(dir).FetchAsync(Request(role), default));
    }

    // ---- Step 4: the CLI arguments ----

    [Fact]
    public void The_cli_is_asked_for_the_role_and_every_key_part_that_is_known_and_json()
    {
        var args = DesigncCliContextSource.Arguments("/d/scripts/designc-core.js", "/reg", Request());

        Assert.Equal(["/d/scripts/designc-core.js", "bundle", "--registry", "/reg", "--role", "builder",
            "--handover", "20261102-abcd1234", "--item", "W-tags", "--task", "T-1",
            "--element", "Tags/Tags API", "--element", "Tags/Tags UI", "--json"], args);
    }

    [Fact]
    public void Absent_key_parts_are_left_out()
    {
        var args = DesigncCliContextSource.Arguments("s.js", "/reg", new(null, null, null, "builder", []));
        Assert.Equal(["s.js", "bundle", "--registry", "/reg", "--role", "builder", "--json"], args);
    }

    // ---- Step 5: running the CLI ----

    private static string FakeNode(string body)
    {
        var dir = TempDir();
        var script = Path.Combine(dir, "node");
        File.WriteAllText(script, "#!/bin/sh\n[ \"$1\" = warm-up ] && exit 0\n" + body.Replace("\r", ""));
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // A parallel fork can hold a just-written script open for a moment (ETXTBSY); one successful exec ends that window.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(script, "warm-up") { RedirectStandardOutput = true })!;
                p.WaitForExit();
                break;
            }
            catch (System.ComponentModel.Win32Exception) when (attempt < 50) { Thread.Sleep(10); }
        }
        return script;
    }

    private static DesigncCliContextSource Cli(string node, TimeSpan? timeout = null) =>
        new("/designc/scripts/designc-core.js", "/reg", node, timeout ?? TimeSpan.FromSeconds(20));

    [Fact]
    public async Task The_cli_source_parses_stdout_and_ignores_stderr()
    {
        var dir = TempDir();
        var json = Path.Combine(dir, "out.json");
        File.WriteAllText(json, Example);
        var node = FakeNode($"echo 'a warning' >&2\ncat '{json}'");

        var fetched = Assert.IsType<BundleFetch.Fetched>(await Cli(node).FetchAsync(Request(), default));
        Assert.StartsWith("sha256:4a57987a", fetched.Bundle.BundleId);
    }

    [Fact]
    public async Task The_cli_source_misses_on_a_failed_exit_even_with_json_on_stdout()
    {
        var dir = TempDir();
        var json = Path.Combine(dir, "out.json");
        File.WriteAllText(json, Example);
        var missed = Assert.IsType<BundleFetch.Missed>(await Cli(FakeNode($"cat '{json}'\nexit 2")).FetchAsync(Request(), default));
        Assert.Equal("designc", missed.Source);
        Assert.Contains("exit 2", missed.Reason);
    }

    [Fact]
    public async Task The_cli_source_misses_on_garbage_output_a_hang_and_a_missing_node()
    {
        Assert.IsType<BundleFetch.Missed>(await Cli(FakeNode("echo not json")).FetchAsync(Request(), default));

        var hang = Assert.IsType<BundleFetch.Missed>(await Cli(FakeNode("sleep 30"), TimeSpan.FromMilliseconds(300)).FetchAsync(Request(), default));
        Assert.Contains("timed out", hang.Reason);

        var gone = Assert.IsType<BundleFetch.Missed>(await Cli("/no/such/node").FetchAsync(Request(), default));
        Assert.Equal("designc", gone.Source);
    }
}
