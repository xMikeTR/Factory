using System.Diagnostics;
using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Harness.Registry;
using Factory.Harness.Policy;
using Factory.Integration.Tests.Support;
using Factory.Integration.Tests.Workflow;
using Factory.S2;
using Microsoft.Extensions.AI;
using Microsoft.FSharp.Collections;
using Xunit;
using static Factory.Integration.Tests.Support.ScriptedChatClient;
using FsCore = global::Factory.Core.Core;

namespace Factory.Integration.Tests.Generation;

/// The X3 Done-when: in a cross-element run the agent diffs contain no hand-written client or DTO for a pinned operation (the
/// `generated_boundary` check blocks or reports one), and a contract change regenerates both sides while the agent's files stay as they were.
public sealed class GeneratedBoundaryWorkflowTests
{
    private const string Source = "contracts/openapi/fare-quotes.yaml";
    private const string Server = "Fares Platform/Bookings DB";
    private const string Client = "Fares Platform/Fares API";
    private const string Quotes = "src/Fares.Api/quotes.ts";
    private const string Spec = """
        openapi: 3.0.0
        info: { title: Fare quotes, version: "1" }
        paths:
          /quotes:
            get:
              operationId: listQuotes
              responses: { "200": { description: ok } }
        components:
          schemas:
            Quote: { type: object, properties: { id: { type: string } } }
        """;
    private const string Bad = "export interface Quote { id: string }\nexport const quotes = () => fetch(\"/quotes\");\n";
    private const string Good = "import { createFareQuotesClient } from \"../../generated/contracts/ts/FareQuotes/FareQuotes.client\";\n"
        + "export const quotes = (baseUrl: string) => createFareQuotesClient(baseUrl).listQuotes();\n";

    private sealed class Approver : IHumanChannel
    {
        public Task<MergeApprovalResponse> ApproveAsync(MergeApprovalRequest request, CancellationToken ct) =>
            Task.FromResult(new MergeApprovalResponse(request.RunId, true, "t"));
    }

    private static Handover Shape(Handover b, string spec) => b.WithClientContract(Server, Client, Source, spec);

    private static string Plan(Handover h) => JsonSerializer.Serialize(new
    {
        handover = new { id = h.Id, digest = h.Digest },
        tasks = new[]
        {
            new
            {
                id = "T-001", title = "Show quotes", instructions = "Show the quotes in the portal.",
                elements = new[] { Server, Client }, writes = new[] { "src/Fares.Data/**", "src/Fares.Api/**" }, testWrites = new[] { "tests/Fares.Data.Tests/**" },
                dependsOn = Array.Empty<string>(), covers = new[] { "D-0001" },
                verify = new[] { new { name = "file exists", kind = "invariant", run = $"sh {TeamHarness.BookingStatusTest}", timeoutSeconds = 30 } },
            },
        },
    });

    private static ChatResponse[] Turn(string content) =>
    [
        Call("write_file", new { path = "src/Fares.Data/BookingStatus.cs", content = "public enum BookingStatus { Draft }\n" }),
        Call("write_file", new { path = Quotes, content }),
        Call("declare_done", new { summary = "quotes" }), Say("done"),
    ];

    private static async Task<(TeamHarness H, ScriptedChatClient Model, RunFinished? Result)> RunAsync(QualityMode mode, string spec, bool csharp, params string[] turns)
    {
        var probe = await TeamHarness.CreateAsync(new ScriptedChatClient(), new Limits(8, 3), shapeHandover: b => Shape(b, spec));
        var plan = Plan(probe.Handover);
        probe.Dispose();
        var model = new ScriptedChatClient();
        model.Enqueue(SubmitPlan(plan), Say("submitted"), Say("Approach: add the quotes view"), Call("declare_done", new { summary = "test already on main" }), Say("done"));
        foreach (var t in turns) model.Enqueue(Turn(t));
        var h = await TeamHarness.CreateAsync(model, new Limits(8, 3), codegen: new FakeCodeGenerator(), shapeHandover: b => Shape(b, spec),
            shapeSettings: s => s with { CodegenTypescript = ["."], CodegenCsharp = csharp ? null : [], CodegenHandWritten = mode });
        var result = await WorkflowHost.RunAsync(S2Workflow.Build(h.Services), new StartRun(TeamHarness.RunId, h.WriteHandoverJson()), new Approver(), default);
        return (h, model, result);
    }

    private static async Task<string> GitAsync(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return output;
    }

    private static async Task<IReadOnlyList<GeneratedBoundary.Finding>> WholeDiffFindingsAsync(TeamHarness h)
    {
        var integration = h.Services.Git.IntegrationPath(TeamHarness.RunId);
        var diff = await GitAsync(integration, "diff", "-U0", "main", "--", ".", ":(exclude)generated");
        var (ops, dtos) = CodegenPhase.BoundaryInputs(h.Services.Settings.RunDir(TeamHarness.RunId), ".");
        Assert.NotEmpty(ops);
        var added = Diff.addedLines(diff).Select(f => Tuple.Create(f.Item1, f.Item2));
        return GeneratedBoundary.findings(ListModule.OfSeq(ops), ListModule.OfSeq(dtos.Select(d => Tuple.Create(d.Source, d.Name))), ListModule.OfSeq(added)).ToList();
    }

    [Fact]
    public async Task In_block_mode_a_hand_written_client_and_DTO_fail_verify_and_the_generated_ones_pass()
    {
        var (h, model, result) = await RunAsync(QualityMode.Block, Spec, csharp: false, Bad, Good);
        using var _ = h;
        Assert.True(result?.Status == "ready_for_review", result?.Status + ": " + result?.Detail);
        var rows = h.Rows(LedgerKinds.Verify).Where(e => e.Stage == "generated_boundary").ToList();
        Assert.Equal(["blocked", "clean"], rows.Select(r => r.Outcome));
        Assert.Contains("listQuotes", rows[0].Payload);
        Assert.Contains("\"subject\":\"Quote\"", rows[0].Payload);
        Assert.Contains(model.Requests.SelectMany(r => r), m => m.Text.Contains("What failed last time") && m.Text.Contains("createFareQuotesClient(baseUrl).listQuotes"));
        Assert.DoesNotContain(h.Rows(LedgerKinds.Verify), e => e.Stage == "allow_list" && e.Outcome is "blocked" or "reported");
        Assert.Empty(await WholeDiffFindingsAsync(h));
        Assert.Contains("createFareQuotesClient", await File.ReadAllTextAsync(Path.Combine(h.Services.Git.IntegrationPath(TeamHarness.RunId), Quotes)));
    }

    [Fact]
    public async Task In_report_mode_the_same_code_passes_and_is_reported()
    {
        var (h, _, result) = await RunAsync(QualityMode.Report, Spec, csharp: false, Bad);
        using var _ = h;
        Assert.True(result?.Status == "ready_for_review", result?.Status + ": " + result?.Detail);
        var row = Assert.Single(h.Rows(LedgerKinds.Verify), e => e.Stage == "generated_boundary");
        Assert.Equal("reported", row.Outcome);
        Assert.Contains("listQuotes", row.Payload);
        Assert.Equal(2, (await WholeDiffFindingsAsync(h)).Count); // and the whole diff shows it too
    }

    [Fact]
    public async Task Policy_off_logs_no_boundary_row()
    {
        var (h, _, result) = await RunAsync(QualityMode.Off, Spec, csharp: false, Bad);
        using var _ = h;
        Assert.True(result?.Status == "ready_for_review", result?.Status + ": " + result?.Detail);
        Assert.DoesNotContain(h.Rows(LedgerKinds.Verify), e => e.Stage == "generated_boundary");
    }

    private static async Task<Dictionary<string, string>> FilesAsync(TeamHarness h, bool generated)
    {
        var integration = h.Services.Git.IntegrationPath(TeamHarness.RunId);
        var files = (await GitAsync(integration, "ls-files")).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.StartsWith("generated/contracts/") == generated && (generated || p.StartsWith("src/")));
        return files.ToDictionary(p => p, p => File.ReadAllText(Path.Combine(integration, p)));
    }

    [Fact]
    public async Task A_contract_change_regenerates_both_sides_and_leaves_the_agents_files_alone()
    {
        var (a, _, ra) = await RunAsync(QualityMode.Block, Spec, csharp: true, Good);
        var (b, _, rb) = await RunAsync(QualityMode.Block, Spec + "# a changed contract\n", csharp: true, Good);
        using var _a = a;
        using var _b = b;
        Assert.Equal("ready_for_review", ra?.Status);
        Assert.Equal("ready_for_review", rb?.Status);
        var (genA, genB) = (await FilesAsync(a, true), await FilesAsync(b, true));
        Assert.Equal(genA.Keys.Order(), genB.Keys.Order());
        string[] changed = ["FareQuotes.Dtos.g.cs", "FareQuotes.Server.g.cs", "FareQuotes.Client.g.cs", "FareQuotes.schema.ts", "FareQuotes.client.ts"];
        foreach (var name in changed)
        {
            var key = genA.Keys.Single(k => k.EndsWith(name));
            Assert.NotEqual(genA[key], genB[key]);
        }
        Assert.Equal(await FilesAsync(a, false), await FilesAsync(b, false));
    }
}
