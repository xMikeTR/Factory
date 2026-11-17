using Factory.Core;
using Factory.Harness.Registry;
using Factory.Integration.Tests.Verify;
using Factory.S2;

namespace Factory.Integration.Tests.Support;

using Core = Factory.Core.Core;

/// A TeamHarness whose repo is the fixture UI app (page file, Vitest config with the ui-holdout project, preview helper) and whose
/// sandbox answers the page and holdout test runs without Docker.
public sealed record UiWorkflowSetup(TeamHarness Harness, FakeUiSandboxes Sandboxes, string HandoverJson, Handover Handover, UiHandoverDir Ui)
{
    public const string PageElement = "Fares Platform/BordereauxList";

    public static async Task<UiWorkflowSetup> CreateAsync(ScriptedChatClient model, UiHandoverDir ui, bool holdoutInRegistry = true, Limits? limits = null, Factory.S2.Quality.IQaRunner? qa = null)
    {
        FakeUiSandboxes? fake = null;
        var h = await TeamHarness.CreateAsync(model, limits ?? new Limits(8, 3),
            qa: qa,
            seedRepo: repo => UiPreviewFixture.Copy(Path.Combine(UiPreviewFixture.FixtureRoot, "app"), repo),
            wrapSandboxes: inner => fake = new FakeUiSandboxes(inner, UiSpecStates.ofSpec(File.ReadAllText(Path.Combine(ui.Dir, "ui", "specs", "bordereaux.json"))).ToArray()));
        var uiJson = await HandoverInput.ResolveAsync(ui.Dir, h.StateDir, default);
        var uiHandover = Core.ParseHandover(await File.ReadAllTextAsync(uiJson));
        var b = h.Handover;
        // The page's element: the one task the plan has is linked to it (so it is blamed), and its code areas cover everything that task writes.
        var element = new ElementSpec(ElementId.NewElementId(PageElement), "BordereauxList",
            Core.ToList([Plans.Pattern("src/Fares.Data/**"), Plans.Pattern("src/BordereauxList.tsx"), Plans.Pattern("tests/Fares.Data.Tests/**")]),
            b.Elements.First().Risk, true, Microsoft.FSharp.Core.FSharpOption<string>.None);
        var handover = new Handover(b.Id, b.Digest, Core.ToList(b.Elements.Append(element)), Core.ToList<BoundDecision>([]), b.ProtectedPaths, b.EventVocabulary, b.Relationships, b.AcceptanceSpecs,
            b.WorkItem, b.DesignTokens, b.DesignVersion, b.AcceptanceCriteria, b.LoopSpecs, b.References, b.Contracts, b.Uncovered, b.ApiContracts,
            b.RequirementRisk, b.RequirementDataClass, uiHandover.Ui);
        var json = Path.Combine(h.StateDir, "handovers", "ui-holdout.json");
        Directory.CreateDirectory(Path.GetDirectoryName(json)!);
        await File.WriteAllTextAsync(json, Core.HandoverJson(handover));
        await HandoverInput.CopyUiPreviewsAsync(handover, ui.Dir, HandoverInput.PreviewsFolder(json), default);
        if (holdoutInRegistry)
        {
            var dest = Path.Combine(h.Services.Settings.RegistryPath, "ui", "holdout", "bordereaux.json");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(Path.Combine(UiHandoverDir.Registry, "ui", "holdout", "bordereaux.json"), dest, overwrite: true);
        }
        return new(h, fake!, json, handover, ui);
    }

    /// The scripted model's replies for a one-task plan that writes under src/ (so it is blamed for the page file) and adds one file.
    public static void Script(ScriptedChatClient model, Handover handover)
    {
        var plan = Factory.Integration.Tests.Workflow.S2WorkflowTests.PlanFor(handover)
            .Replace("\"elements\":[\"Fares Platform/Bookings DB\"]", $"\"elements\":[\"{PageElement}\"]")
            .Replace("\"covers\":[\"D-0001\"]", "\"covers\":[]")
            .Replace("\"writes\":[\"src/Fares.Data/**\"]", "\"writes\":[\"src/Fares.Data/**\",\"src/BordereauxList.tsx\"]");
        var check = Core.ValidatePlan(handover, plan, []);
        if (check.Errors.Length > 0) throw new InvalidOperationException(string.Join(" | ", check.Errors));
        model.Enqueue(
            ScriptedChatClient.SubmitPlan(plan), ScriptedChatClient.Say("submitted"),
            ScriptedChatClient.Say("Approach: add BookingStatus.cs"),
            ScriptedChatClient.Call("declare_done", new { summary = "test already on main" }), ScriptedChatClient.Say("done"),
            ScriptedChatClient.Call("write_file", new { path = "src/Fares.Data/BookingStatus.cs", content = "public enum BookingStatus { Draft }\n" }),
            ScriptedChatClient.Call("write_file", new { path = "src/BordereauxList.tsx", content = File.ReadAllText(Path.Combine(UiPreviewFixture.FixtureRoot, "app", "src", "BordereauxList.tsx")) + "\n// reviewed\n" }),
            ScriptedChatClient.Call("declare_done", new { summary = "added BookingStatus" }), ScriptedChatClient.Say("done"));
    }
}
