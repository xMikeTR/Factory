using Factory.Harness;
using Factory.Harness.Registry;
using Factory.Harness.Verify;
using Factory.Integration.Tests.Support;
using Microsoft.FSharp.Collections;
using Xunit;

namespace Factory.Integration.Tests.Verify;

public class AcceptanceVerifyTests
{
    private const string Spec = "tests/acceptance/quote-form/AC-0107.spec.ts";

    private const string Report = """
        { "suites": [
            { "title": "AC-0107.spec.ts", "file": "quote-form/AC-0107.spec.ts", "suites": [
                { "title": "AC-0107 Quote Form", "file": "quote-form/AC-0107.spec.ts", "specs": [
                    { "title": "ready", "ok": true, "file": "quote-form/AC-0107.spec.ts", "tests": [] },
                    { "title": "error", "ok": false, "file": "quote-form/AC-0107.spec.ts", "tests": [
                        { "results": [ { "attachments": [ { "name": "trace", "path": "test-results/trace.zip" } ] } ] } ] } ] } ] },
            { "title": "other", "file": "quote-api/AC-0200.spec.ts", "specs": [
                { "title": "ok", "ok": true, "file": "quote-api/AC-0200.spec.ts", "tests": [] } ] } ] }
        """;

    [Fact]
    public void A_failing_spec_is_reported_once_at_its_frozen_path_with_its_trace()
    {
        var failures = PlaywrightReport.Failures(Report, [Spec, "tests/acceptance/quote-api/AC-0200.spec.ts"]);

        var f = Assert.Single(failures);
        Assert.Equal(Spec, f.File);
        Assert.Equal("test-results/trace.zip", f.Trace);
    }

    [Fact]
    public void A_report_with_only_passing_specs_has_no_failures()
    {
        Assert.Empty(PlaywrightReport.Failures("""{"suites":[{"file":"a.spec.ts","specs":[{"ok":true,"file":"a.spec.ts","tests":[]}]}]}""", []));
    }

    [Fact]
    public void Acceptance_files_are_hashed_like_designc_hashed_them()
    {
        var dir = Path.Combine(Path.GetTempPath(), "s2-ac-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "tests", "acceptance", "quote-form"));
        File.WriteAllText(Path.Combine(dir, Spec), "// GENERATED\n");

        var hashes = AcceptanceFiles.Hashes(dir);

        Assert.Equal([(Spec, DesigncHandoverReader.Hash16("// GENERATED\n"))], hashes);
        Assert.Empty(AcceptanceFiles.Hashes(Path.Combine(dir, "nowhere")));
    }

    [Fact]
    public void A_failing_acceptance_row_is_projected_on_the_work_item_and_validates_against_designc()
    {
        var handover = new HandoverRef("20261013-aa", [], "W-1");
        var run = new List<LedgerEvent>
        {
            new("run-1", "S2", "integration_verify", LedgerKinds.UiAcceptanceFailed, TaskId: "T-001", Element: "Pricing/Quote Form",
                Outcome: "failed", Payload: """{"ac":"AC-0107","spec":"tests/acceptance/quote-form/AC-0107.spec.ts","commit":"abcdef1234"}""") { TraceId = null },
            new("run-1", "S2", "integration_verify", LedgerKinds.UiAcceptanceFailed, TaskId: "T-003", Element: "Pricing/Quote Form",
                Outcome: "failed", Payload: """{"ac":"AC-0107","spec":"tests/acceptance/quote-form/AC-0107.spec.ts","commit":"abcdef1234"}""") { TraceId = null },
        };

        var rows = RegistryLedgerProjector.Project(run, handover);

        var row = Assert.Single(rows);
        Assert.Equal("pipeline.jsonl", row.Stream);
        Assert.Empty(DesigncSchema.Validate(row.Line));
        Assert.Contains("\"task\":\"W-1\"", row.Line);
        Assert.Empty(RegistryLedgerProjector.Project(run, new HandoverRef("20261013-aa", [])));
    }
}
