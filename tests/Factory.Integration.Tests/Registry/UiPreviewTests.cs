using System.Text.Json;
using Factory.Harness.Registry;
using Xunit;

namespace Factory.Integration.Tests.Registry;

public class UiPreviewTests
{
    private const string Spec = """
        { "screen": "bordereaux", "template": "list-detail", "data": {},
          "slots": { "header": [ { "id": "hdr", "component": "PageHeader" } ],
                     "header.actions": [ { "id": "export", "component": "Button" } ],
                     "main": [ { "component": "DataTable" } ] },
          "states": { "ready": { "initial": true }, "error": { "failed": ["x"] } } }
        """;

    [Fact]
    public void A_state_expects_a_picture_and_an_aria_tree_and_each_placement_with_an_id_a_picture_per_state()
    {
        Assert.Equal(
        [
            "error-chromium-linux.png", "error.aria.yml", "placements/error__export-chromium-linux.png", "placements/error__hdr-chromium-linux.png",
            "placements/ready__export-chromium-linux.png", "placements/ready__hdr-chromium-linux.png", "ready-chromium-linux.png", "ready.aria.yml",
        ], UiPreview.ExpectedFiles(Spec));
    }

    [Fact]
    public void A_placement_without_an_id_has_no_picture_of_its_own()
    {
        Assert.DoesNotContain(UiPreview.ExpectedFiles(Spec), f => f.Contains("DataTable") || f.Contains("__-"));
    }

    [Theory]
    [InlineData("bordereaux", true)]
    [InlineData("a-1", true)]
    [InlineData("", false)]
    [InlineData("../x", false)]
    [InlineData("Bord", false)]
    [InlineData("a b", false)]
    public void A_screen_name_is_a_safe_folder_name(string screen, bool ok) => Assert.Equal(ok, UiPreview.IsScreenName(screen));

    [Fact]
    public void The_generated_test_is_the_kits_preview_test_importing_the_apps_registry()
    {
        var text = UiPreview.PreviewTest("bordereaux");

        Assert.Contains("import spec from \"../specs/bordereaux.json\";", text);
        Assert.Contains("import { components, layouts } from \"../registry\";", text);
        Assert.Contains("import { previewTests } from \"../preview/vitest-ui\";", text);
        Assert.Contains("import.meta.glob(\"../manifests/*.json\"", text);
        Assert.Contains("previewTests(spec as any, { components, layouts, manifests, previewsRel: \"../previews\" });", text);
        Assert.DoesNotContain("mock", text);
    }

    [Fact]
    public void The_default_command_updates_only_the_preview_project_and_names_the_screen()
    {
        Assert.Equal("npx vitest run --project ui-preview -u --reporter=json bordereaux.preview", UiPreview.Command("bordereaux", null));
        Assert.Equal("npm run x -- bordereaux", UiPreview.Command("bordereaux", "npm run x -- {screen}"));
        Assert.Equal("custom", UiPreview.Command("bordereaux", "custom"));
    }

    [Fact]
    public void Designc_is_asked_for_a_check_that_records_nothing()
    {
        Assert.Equal(["-m", "designc", "check", "/reg", "--json", "--no-record"], UiPreview.CheckArguments("/reg"));
    }

    [Fact]
    public void A_ui_clause_for_this_screen_or_for_the_pack_refuses_but_another_screens_does_not()
    {
        var json = """
            { "errors": [], "findings": [], "gate": { "ready": false, "clauses": [
              { "kind": "ui", "text": "bordereaux: bad", "path": "ui/specs/bordereaux.json", "screen": "bordereaux" },
              { "kind": "ui", "text": "other: bad", "path": "ui/specs/other.json", "screen": "other" },
              { "kind": "ui", "text": "ui/pack/ is not pinned" },
              { "kind": "acceptance", "text": "unrelated" } ] } }
            """;

        Assert.Equal(["bordereaux: bad", "ui/pack/ is not pinned"], UiPreview.SpecRefusals(json, "bordereaux"));
    }

    [Fact]
    public void A_clean_check_has_no_refusals_and_unreadable_output_is_one()
    {
        Assert.Empty(UiPreview.SpecRefusals("""{ "gate": { "clauses": [] } }""", "bordereaux"));
        Assert.Single(UiPreview.SpecRefusals("not json", "bordereaux"));
    }

    [Fact]
    public void Warnings_around_designcs_json_do_not_hide_its_clauses()
    {
        var out1 = "DeprecationWarning: x\n{ \"gate\": { \"clauses\": [ { \"kind\": \"ui\", \"text\": \"pack bad\" } ] } }\n";

        Assert.Equal(["pack bad"], UiPreview.SpecRefusals(out1, "bordereaux"));
    }

    [Theory]
    [InlineData(0, false, """{"success":true,"numFailedTests":0,"numPassedTests":5}""", UiPreviewOutcome.Rendered)]
    [InlineData(1, false, """{"success":false,"numFailedTests":2,"numPassedTests":3}""", UiPreviewOutcome.CouldNotRun)]
    [InlineData(1, false, "noise before {\"success\":false,\"numFailedTests\":1,\"numPassedTests\":0} after", UiPreviewOutcome.CouldNotRun)]
    [InlineData(1, false, "Error: cannot find config", UiPreviewOutcome.Error)]
    [InlineData(0, false, "not json", UiPreviewOutcome.Error)]
    [InlineData(0, false, """{"success":true,"numFailedTests":0,"numPassedTests":0}""", UiPreviewOutcome.Error)]
    [InlineData(124, true, "", UiPreviewOutcome.Error)]
    public void Test_failures_are_could_not_run_and_anything_unreadable_is_an_error(int exit, bool timedOut, string output, UiPreviewOutcome expected) =>
        Assert.Equal(expected, UiPreview.Interpret(exit, timedOut, output));

    [Fact]
    public void The_row_names_the_screen_the_spec_bytes_the_files_and_the_image_and_is_written_as_s2()
    {
        var files = new Dictionary<string, string> { ["ready.aria.yml"] = new('b', 64), ["ready-chromium-linux.png"] = new('a', 64) };
        var row = UiPreview.Row("bordereaux", new string('c', 64), files, "sha256:img", DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));

        Assert.Equal("designc.jsonl", row.Stream);
        using var doc = JsonDocument.Parse(row.Line);
        var r = doc.RootElement;
        Assert.Equal("ui_preview_rendered", r.GetProperty("kind").GetString());
        Assert.Equal(RegistryLedgerProjector.By, r.GetProperty("by").GetString());
        Assert.Equal("bordereaux", r.GetProperty("screen").GetString());
        Assert.Equal(new string('c', 64), r.GetProperty("spec_hash").GetString());
        Assert.Equal("sha256:img", r.GetProperty("image").GetString());
        Assert.Equal(["ready-chromium-linux.png", "ready.aria.yml"], r.GetProperty("files").EnumerateObject().Select(p => p.Name));
        Assert.Equal(row.EventId, r.GetProperty("event_id").GetString());
        Assert.Equal(1_700_000_000_000, r.GetProperty("ts").GetInt64());
    }

    [Fact]
    public void Vitest_5_names_its_json_report_file_in_the_output()
    {
        Assert.Equal("/tmp/w/.vitest/json/output.json", UiPreview.ReportPath("x\nJSON report written to /tmp/w/.vitest/json/output.json\n"));
        Assert.Null(UiPreview.ReportPath("{\"success\":true}"));
    }

    [Fact]
    public void Hashes_are_lower_case_sha256_hex()
    {
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", UiPreview.Sha256Hex([]));
    }
}
