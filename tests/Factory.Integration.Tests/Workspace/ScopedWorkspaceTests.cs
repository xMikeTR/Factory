using Factory.Core;
using Factory.Harness;
using Factory.Harness.Sandbox;
using Xunit;

namespace Factory.Integration.Tests.Workspace;

public class ScopedWorkspaceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ws-").FullName;

    [Fact]
    public async Task Commands_run_in_the_sandbox_without_host_secrets()
    {
        Environment.SetEnvironmentVariable("FACTORY_GATEWAY_KEY", "sekrit-gateway");
        try
        {
            var ws = new ScopedWorkspace(_root, [], [], ["printenv"], new TurnSignals(), new LocalSandbox());
            var output = await ws.RunCommand("printenv");
            Assert.StartsWith("exit 0", output);
            Assert.DoesNotContain("sekrit-gateway", output);
            Assert.Contains("PATH=", output);
        }
        finally { Environment.SetEnvironmentVariable("FACTORY_GATEWAY_KEY", null); }
    }

    [Fact]
    public async Task Without_a_sandbox_commands_are_refused()
    {
        var ws = new ScopedWorkspace(_root, [], [], ["printenv"], new TurnSignals());
        Assert.StartsWith("DENIED", await ws.RunCommand("printenv"));
    }

    [Fact]
    public async Task The_implementer_gets_check_quality_only_when_the_run_measures_quality()
    {
        Assert.DoesNotContain("check_quality", new ScopedWorkspace(_root, [], [], [], new TurnSignals()).ImplementerTools().Select(t => t.Name));

        var tools = new ScopedWorkspace(_root, [], [], [], new TurnSignals(), qualityCheck: _ => Task.FromResult("- A.cs:3 A.F: Cyclomatic 9")).ImplementerTools();
        var check = tools.OfType<Microsoft.Extensions.AI.AIFunction>().Single(t => t.Name == "check_quality");

        Assert.Contains("A.F: Cyclomatic 9", (await check.InvokeAsync())?.ToString());
        Assert.DoesNotContain("check_quality", new ScopedWorkspace(_root, [], [], [], new TurnSignals(), qualityCheck: _ => Task.FromResult("")).ExecutorTools().Select(t => t.Name));
    }

    private sealed class RecordingSandbox : ICommandSandbox
    {
        public List<string> Commands { get; } = [];
        public string Kind => "fake";
        public Task<ProcessResult> RunAsync(string command, string workdir, TimeSpan timeout, IReadOnlyDictionary<string, string> env, CancellationToken ct)
        {
            Commands.Add(command);
            return Task.FromResult(new ProcessResult(0, "page: Quotes", TimeSpan.Zero, false));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task The_implementer_gets_playwright_cli_only_for_a_self_check_run()
    {
        var ws = new ScopedWorkspace(_root, [], [], [], new TurnSignals());
        Assert.DoesNotContain("playwright_cli", ws.ImplementerTools().Select(t => t.Name));
        Assert.Contains("playwright_cli", ws.ImplementerTools(selfCheck: true).Select(t => t.Name));
        Assert.DoesNotContain("playwright_cli", ws.ReviewerTools().Concat(ws.ReadOnlyTools()).Select(t => t.Name));
    }

    [Fact]
    public async Task Playwright_cli_runs_allow_listed_subcommands_against_localhost_only()
    {
        var sandbox = new RecordingSandbox();
        var ws = new ScopedWorkspace(_root, [], [], [], new TurnSignals(), sandbox);

        Assert.StartsWith("exit 0", await ws.PlaywrightCli("open http://localhost:3000/quotes"));
        Assert.StartsWith("exit 0", await ws.PlaywrightCli("snapshot"));
        Assert.Equal(["playwright-cli open http://localhost:3000/quotes", "playwright-cli snapshot"], sandbox.Commands);

        Assert.StartsWith("DENIED", await ws.PlaywrightCli("open https://prod.example.com"));
        Assert.StartsWith("DENIED", await ws.PlaywrightCli("install --skills"));
        Assert.StartsWith("DENIED", await ws.PlaywrightCli("snapshot; rm -rf /"));
        Assert.StartsWith("DENIED", await new ScopedWorkspace(_root, [], [], [], new TurnSignals()).PlaywrightCli("snapshot"));
        Assert.Equal(2, sandbox.Commands.Count);
    }

    [Fact]
    public void Repro_and_diagnosis_cannot_write_and_report_through_their_own_tool()
    {
        var signals = new TurnSignals();
        var ws = new ScopedWorkspace(_root, [], [], [], signals);
        foreach (var tools in new[] { ws.ReproTools(), ws.DiagnosisTools() })
            Assert.DoesNotContain(tools.Select(t => t.Name), n => n is "write_file" or "delete_file" or "run_command");
        var diagnose = ws.DiagnosisTools().OfType<Microsoft.Extensions.AI.AIFunction>().Single(t => t.Name == "submit_diagnosis");
        diagnose.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments { ["finding"] = "button is outside the form" }).AsTask().Wait();
        Assert.Equal("button is outside the form", signals.Diagnosis);
    }

    [Fact]
    public async Task The_reviewer_can_read_and_submit_findings_but_not_write_run_or_park()
    {
        var signals = new TurnSignals();
        var tools = new ScopedWorkspace(_root, [], [], [], signals).ReviewerTools();
        Assert.Equal(["list_files", "read_file", "submit_review"], tools.Select(t => t.Name).Order());
        var submit = tools.OfType<Microsoft.Extensions.AI.AIFunction>().Single(t => t.Name == "submit_review");

        var refused = await submit.InvokeAsync(new()
        {
            ["findings"] = System.Text.Json.JsonDocument.Parse("""[{"category":"style","file":"src/A.cs","line":3,"message":"x"}]""").RootElement.Clone(),
        });
        Assert.Null(signals.Review);
        Assert.Contains("category 'style'", refused?.ToString());

        await submit.InvokeAsync(new()
        {
            ["findings"] = System.Text.Json.JsonDocument.Parse("""[{"category":"weak_tests","file":"tests/A.cs","line":7,"message":"asserts nothing"}]""").RootElement.Clone(),
        });
        var finding = Assert.Single(signals.Review!);
        Assert.Equal((FindingCategory.WeakTests, "tests/A.cs", 7), (finding.Category, finding.File, finding.Line));
    }

    [Fact]
    public async Task Submit_approach_records_the_note_and_the_validated_selection_and_reports_rejections()
    {
        var signals = new TurnSignals();
        var ws = new ScopedWorkspace(_root, [], [], [], signals);
        var tools = ws.TaskPlannerTools(noms =>
        {
            var ok = noms.Where(n => n.Path != "src/Missing.cs").ToList();
            return new Selection(
                Microsoft.FSharp.Collections.ListModule.OfSeq(ok.Select(n => new ContextEntry(n.Path, ContextSource.NewNominated(n.Reason)))),
                Microsoft.FSharp.Collections.ListModule.OfSeq(noms.Except(ok).Select(n => Tuple.Create(n.Path, "does not exist"))));
        });
        Assert.Contains(tools, t => t.Name == "raise_needs_decision");
        var submit = tools.OfType<Microsoft.Extensions.AI.AIFunction>().Single(t => t.Name == "submit_approach");

        var answer = await submit.InvokeAsync(new()
        {
            ["note"] = "change Quote.cs first",
            ["context"] = System.Text.Json.JsonDocument.Parse("""[{"path":"src/A.cs","reason":"calls Quote"},{"path":"src/Missing.cs","reason":"x"}]""").RootElement.Clone(),
        });

        Assert.Equal("change Quote.cs first", signals.ApproachNote);
        Assert.Equal(["src/A.cs"], signals.ContextSelection!.Entries.Select(e => e.Path));
        Assert.Contains("src/Missing.cs", answer?.ToString());
        Assert.Contains("does not exist", answer?.ToString());
    }

    [Fact]
    public async Task Submit_plan_takes_the_plan_schema_records_each_plan_and_returns_the_validators_errors()
    {
        var schema = System.Text.Json.JsonDocument.Parse("""{"$schema":"x","$id":"y","type":"object","required":["tasks"],"properties":{"tasks":{"type":"array"}}}""").RootElement;
        var signals = new TurnSignals();
        var ws = new ScopedWorkspace(_root, [], [], [], signals);
        var submit = ws.PlannerTools(schema, plan => plan.Contains("\"bad\"") ? ["T-001 is bad"] : []).OfType<Microsoft.Extensions.AI.AIFunction>().Single(t => t.Name == "submit_plan");

        Assert.Equal("array", submit.JsonSchema.GetProperty("properties").GetProperty("tasks").GetProperty("type").GetString());
        Assert.False(submit.JsonSchema.TryGetProperty("$schema", out _));

        var bad = await submit.InvokeAsync(new() { ["tasks"] = new[] { "bad" } });
        Assert.Contains("T-001 is bad", bad?.ToString());
        Assert.Contains("bad", signals.SubmittedPlan);

        var good = await submit.InvokeAsync(new() { ["tasks"] = new[] { "good" } });
        Assert.StartsWith("Accepted", good?.ToString());
        Assert.Contains("good", signals.SubmittedPlan);

        await submit.InvokeAsync(new() { ["tasks"] = new[] { "bad" } });
        var refused = await submit.InvokeAsync(new() { ["tasks"] = new[] { "good" } });
        Assert.StartsWith("DENIED", refused?.ToString());
        Assert.Contains("bad", signals.SubmittedPlan); // the last plan the validator saw stands
    }

    [Fact]
    public async Task Raise_needs_decision_refuses_a_question_without_a_real_choice()
    {
        var signals = new TurnSignals();
        var ws = new ScopedWorkspace(_root, [], [], [], signals);
        var raise = ws.ReadOnlyTools().OfType<Microsoft.Extensions.AI.AIFunction>().Single(t => t.Name == "raise_needs_decision");
        var a = new { id = "A", summary = "3 attempts", consequence = "a 4th conflict fails the write" };
        var b = new { id = "B", summary = "no limit", consequence = "writes may spin" };

        var one = await raise.InvokeAsync(new() { ["question"] = "Retry limit?", ["options"] = new[] { a } });
        Assert.Contains("at least two options", one?.ToString());
        Assert.Null(signals.NeedsDecision);

        var unknown = await raise.InvokeAsync(new() { ["question"] = "Retry limit?", ["options"] = new[] { a, b }, ["recommended"] = "C" });
        Assert.Contains("recommended 'C'", unknown?.ToString());
        Assert.Null(signals.NeedsDecision);

        var ok = await raise.InvokeAsync(new() { ["question"] = "Retry limit?", ["options"] = new[] { a, b }, ["recommended"] = "A" });
        Assert.StartsWith("Recorded", ok?.ToString());
        Assert.Equal("Retry limit?", signals.NeedsDecision?.Question);
        Assert.Equal(["A", "B"], signals.NeedsDecision!.Options.Select(o => o.Id));
        Assert.Equal("A", signals.NeedsDecision.Recommended);
    }

    private string Reference()
    {
        var dir = Directory.CreateTempSubdirectory("ref-").FullName;
        Directory.CreateDirectory(Path.Combine(dir, "Views"));
        File.WriteAllText(Path.Combine(dir, "Views", "Calc.cs"), "Math.Round(x)");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(dir)!, Path.GetFileName(dir) + "-secret.txt"), "outside");
        _refs.Add(dir);
        return dir;
    }

    private readonly List<string> _refs = [];

    private ScopedWorkspace WithReference(string dir) =>
        new(_root, [], [], [], new TurnSignals(), references: new Dictionary<string, string> { ["wpf"] = dir });

    [Fact]
    public void A_reference_is_read_by_key_and_listed_under_it()
    {
        var ws = WithReference(Reference());
        Assert.Equal("Math.Round(x)", ws.ReadReference("wpf", "Views/Calc.cs"));
        Assert.Equal("Views/Calc.cs", ws.ListReference("wpf", "."));
    }

    [Fact]
    public void A_reference_read_cannot_leave_the_reference()
    {
        var dir = Reference();
        var ws = WithReference(dir);
        var sibling = Path.GetFileName(dir) + "-secret.txt";
        Assert.StartsWith("DENIED", ws.ReadReference("wpf", "../" + sibling));
        Assert.StartsWith("DENIED", ws.ReadReference("wpf", Path.Combine(Path.GetDirectoryName(dir)!, sibling)));
        Assert.StartsWith("DENIED", ws.ListReference("wpf", ".."));
        Assert.StartsWith("DENIED", ws.ReadReference("wpf", ".git/config"));
    }

    [Fact]
    public void A_symlink_inside_a_reference_cannot_lead_out_of_it()
    {
        var dir = Reference();
        var outside = Path.Combine(Path.GetDirectoryName(dir)!, Path.GetFileName(dir) + "-secret.txt");
        File.CreateSymbolicLink(Path.Combine(dir, "link.txt"), outside);
        Assert.StartsWith("DENIED", WithReference(dir).ReadReference("wpf", "link.txt"));
    }

    [Fact]
    public void An_unknown_reference_key_is_refused()
    {
        Assert.StartsWith("ERROR", WithReference(Reference()).ReadReference("web", "Views/Calc.cs"));
    }

    [Fact]
    public void Reference_tools_go_to_the_roles_that_build_and_only_when_the_run_has_references()
    {
        string[] names(IEnumerable<Microsoft.Extensions.AI.AITool> t) => t.Select(x => x.Name).ToArray();
        var without = new ScopedWorkspace(_root, [], [], [], new TurnSignals());
        Assert.DoesNotContain("read_reference", names(without.ImplementerTools()));
        Assert.DoesNotContain("read_reference", names(without.TestAuthorTools()));

        var ws = WithReference(Reference());
        Assert.Contains("read_reference", names(ws.ImplementerTools()));
        Assert.Contains("list_reference", names(ws.ImplementerTools()));
        Assert.Contains("read_reference", names(ws.TestAuthorTools()));
        Assert.DoesNotContain("read_reference", names(ws.ReviewerTools()));
        Assert.DoesNotContain("read_reference", names(ws.ReadOnlyTools()));
        Assert.DoesNotContain("read_reference", names(ws.QaAuthorTools()));
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        foreach (var r in _refs)
        {
            Directory.Delete(r, recursive: true);
            File.Delete(Path.Combine(Path.GetDirectoryName(r)!, Path.GetFileName(r) + "-secret.txt"));
        }
    }
}
