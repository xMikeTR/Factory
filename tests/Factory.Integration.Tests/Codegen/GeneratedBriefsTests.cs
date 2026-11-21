using FsCore = global::Factory.Core.Core;
using Factory.Core;
using Factory.Harness;
using Factory.Harness.Registry;
using Factory.Integration.Tests.Support;
using Factory.Integration.Tests.Workflow;
using Factory.S2;
using Factory.S2.Teams;
using Xunit;
using static Factory.Integration.Tests.Support.ScriptedChatClient;

namespace Factory.Integration.Tests.Generation;

/// The briefs name the generated code to use (X3): the task brief in its stable part and limited to the task's elements, the planner turn
/// brief after the handover block, and both prompts forbid hand-writing what was generated.
public sealed class GeneratedBriefsTests
{
    private const string Source = "contracts/openapi/fare-quotes.yaml";
    private const string Server = "Fares Platform/Bookings DB";
    private const string Client = "Fares Platform/Fares API";
    private static readonly (string Repo, string Path, string Source)[] Files =
    [
        (".", "generated/contracts/csharp/FareQuotes/FareQuotes.Dtos.g.cs", Source),
        (".", "generated/contracts/csharp/FareQuotes/FareQuotes.Server.g.cs", Source),
        (".", "generated/contracts/ts/FareQuotes/FareQuotes.schema.ts", Source),
        (".", "generated/contracts/ts/FareQuotes/FareQuotes.client.ts", Source),
    ];

    private static async Task<Handover> HandoverAsync() =>
        (await new DesigncHandoverReader().LoadAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "handover"), new S2Config([]), CancellationToken.None))
        .WithClientContract(Server, Client, Source, "openapi: 3.0.0\n");

    [Fact]
    public async Task The_task_brief_names_the_generated_code_in_its_stable_part_for_the_tasks_elements_only()
    {
        var handover = await HandoverAsync();
        var serverTask = Briefs.Task(handover, Plans.Task("T-004"), [], null, "x failed", null, generated: Files);
        Assert.Contains("## Generated contract code (read-only", serverTask.Stable);
        Assert.Contains("implement `IFareQuotesController` from `generated/contracts/csharp/FareQuotes/FareQuotes.Server.g.cs`", serverTask.Stable);
        Assert.DoesNotContain("createFareQuotesClient", serverTask.Text); // the caller is another task's element
        Assert.DoesNotContain("Generated contract code", serverTask.Turn);

        var none = Briefs.Task(handover, Plans.Task("T-004"), [], null, null, null);
        Assert.DoesNotContain("Generated contract code", none.Text);
        Assert.Equal(none.Text, Briefs.Task(handover, Plans.Task("T-004"), [], null, null, null, generated: []).Text);
    }

    [Fact]
    public async Task A_calling_task_is_told_to_use_the_generated_client()
    {
        var handover = await HandoverAsync();
        var t = Plans.Task("T-004");
        var task = new PlannedTask(t.Id, t.Title, t.Instructions, FsCore.ToList([ElementId.NewElementId(Client)]), t.Writes, t.TestWrites, t.DependsOn, t.Covers, t.Verify);
        var text = Briefs.Task(handover, task, [], null, null, null, generated: Files).Stable;
        Assert.Contains("call `createFareQuotesClient(baseUrl).listQuotes(...)` from `generated/contracts/ts/FareQuotes/FareQuotes.client.ts`", text);
    }

    [Fact]
    public void Both_prompts_forbid_hand_writing_what_was_generated()
    {
        var planner = Briefs.Prompt("planner");
        Assert.Contains("Code under `generated/contracts/**` is generated from the pinned contracts and is protected.", planner);
        Assert.Contains("never plan writes there or a hand-written client or DTO for a generated operation", planner.Replace("\n  ", " "));
        var implementer = Briefs.Prompt("implementer").Replace("\n  ", " ");
        Assert.Contains("If your brief has \"Generated contract code\", implement the generated interface and call the generated client it names;", implementer);
        Assert.Contains("never write your own HTTP client, fetch call or DTO type for those operations: verify flags it.", implementer);
    }

    private static async Task<string> PlannerBriefAsync(bool codegen)
    {
        Handover Shape(Handover b) => b.WithClientContract(Server, Client, Source, "openapi: 3.0.0\n");
        var probe = await TeamHarness.CreateAsync(new ScriptedChatClient(), new Limits(8, 3), shapeHandover: Shape);
        var plan = S2WorkflowTests.PlanFor(probe.Handover);
        probe.Dispose();
        var model = new ScriptedChatClient();
        model.Enqueue(SubmitPlan(plan), Say("submitted"));
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3), codegen: codegen ? new FakeCodeGenerator() : null, shapeHandover: Shape,
            shapeSettings: s => s with { CodegenOn = codegen });
        try
        {
            await WorkflowHost.RunAsync(S2Workflow.Build(h.Services), new StartRun(TeamHarness.RunId, h.WriteHandoverJson()), new Approver(), default);
        }
        catch (Exception) { /* the script ends after the plan; only the planner's first request matters */ }
        return string.Join("\n", model.Requests[0].Select(m => m.Text));
    }

    [Fact]
    public async Task The_planner_brief_carries_the_section_after_the_handover_and_is_unchanged_without_a_manifest()
    {
        var with = await PlannerBriefAsync(codegen: true);
        var at = with.IndexOf("## Generated contract code (read-only; the Factory wrote it before you plan)", StringComparison.Ordinal);
        Assert.True(at > with.IndexOf("## Handover package", StringComparison.Ordinal), with);
        Assert.Contains("Tasks implement the generated server interfaces and call the generated clients; never plan a write under `generated/contracts/**`", with);
        Assert.Contains("- `listQuotes` (GET /quotes, contracts/openapi/fare-quotes.yaml), served by Fares Platform/Bookings DB: implement `IFareQuotesController`", with);

        var without = await PlannerBriefAsync(codegen: false);
        Assert.DoesNotContain("Generated contract code", without);
        Assert.EndsWith("```", without.TrimEnd());
    }

    private sealed class Approver : IHumanChannel
    {
        public Task<MergeApprovalResponse> ApproveAsync(MergeApprovalRequest request, CancellationToken ct) =>
            Task.FromResult(new MergeApprovalResponse(request.RunId, true, "t"));
    }
}
