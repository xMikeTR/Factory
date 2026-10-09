using Factory.Core;
using Factory.Harness;
using Factory.Harness.Policy;
using Factory.Integration.Tests.Support;
using Factory.S2;
using Factory.S2.Git;
using Factory.S2.Teams;
using Xunit;
using static Factory.Integration.Tests.Support.ScriptedChatClient;

namespace Factory.Integration.Tests.Teams;

/// The UI guard is a verify gate: a green task that takes a UI feature away parks for a designer before it can pass.
public class UiGuardLoopTests
{
    private static readonly IReadOnlyDictionary<string, DecisionAnswer> NoAnswers = new Dictionary<string, DecisionAnswer>();
    private const string NewFile = "src/Pricing.Data/QuoteStatus.cs";
    private const string Page = "src/Pricing.Data/Page.tsx";
    private const string WithButton = "export const P = () => <button data-testid=\"export-btn\" />;\n";
    private const string WithoutButton = "export const P = () => <p />;\n";

    private static PlannedTask T001() => Plans.Task("T-001", Plans.Auto("file exists", $"test -f {NewFile}"));

    private static readonly QualityPolicy UiOn = new(QualityMode.Off, new QualityThresholds(15, 30.0, 80), Ui: new UiPolicy([], []));

    /// Plan, test author, then an implementer turn that adds the file and rewrites the page without its button, then `more`.
    private static ScriptedChatClient Model(params Microsoft.Extensions.AI.ChatResponse[] more) => new([
        Say("Approach: add QuoteStatus.cs"), Call("declare_done", new { summary = "verify block already fails" }), Say("done"),
        Call("write_file", new { path = NewFile, content = "public enum QuoteStatus { Draft }\n" }),
        Call("write_file", new { path = Page, content = WithoutButton }),
        Call("declare_done", new { summary = "added" }), Say("done"),
        .. more]);

    /// The integration branch already has a page with an export button.
    private static async Task<TeamHarness> HarnessAsync(ScriptedChatClient model)
    {
        var h = await TeamHarness.CreateAsync(model, new Limits(8, 3), qualityPolicy: UiOn);
        var wt = await h.Services.Git.EnsureTeamAsync(TeamHarness.RunId, "team-T-001", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(wt, Page), WithButton);
        await h.Services.Git.CommitAllAsync(wt, "page", CancellationToken.None);
        await h.GitIn(h.Services.Git.IntegrationPath(TeamHarness.RunId), "merge", "--ff-only", GitWorkspace.TeamBranch(TeamHarness.RunId, "team-T-001"));
        return h;
    }

    private static Task<TeamResult> RunAsync(TeamHarness h, IReadOnlyDictionary<string, DecisionAnswer>? answers = null) =>
        new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(T001()), answers ?? NoAnswers, default);

    private static Dictionary<string, DecisionAnswer> Answer(string option, string note) => new() { ["T-001"] = new("T-001", option, note) };

    [Fact]
    public async Task A_green_task_that_removes_a_ui_feature_parks_with_the_removal_named()
    {
        using var h = await HarnessAsync(Model());

        var result = await RunAsync(h);

        Assert.Equal(TeamStatus.Blocked, result.Status);
        Assert.Equal("ui", result.Blocked!.Source);
        Assert.Equal(["A", "B", "C"], result.Blocked.Options.Select(o => o.Id));
        Assert.Contains("data-testid \"export-btn\" existed and is gone", result.Blocked.Question);
        Assert.Contains(h.Rows(LedgerKinds.Verify), e => e.Stage == "ui" && e.Outcome == "parked");
        Assert.Empty(h.Rows(LedgerKinds.Review)); // never reached the review
    }

    [Fact]
    public async Task Accepting_the_removal_is_ledgered_and_the_task_goes_on_to_pass()
    {
        var model = Model(Call("declare_done", new { summary = "removal accepted" }), Say("done"));
        using var h = await HarnessAsync(model);
        Assert.Equal(TeamStatus.Blocked, (await RunAsync(h)).Status);

        var result = await RunAsync(h, Answer("A", "the export moved to the toolbar"));

        Assert.Equal(TeamStatus.Passed, result.Status);
        var accepted = Assert.Single(h.Rows(LedgerKinds.Verify), e => e.Stage == "ui" && e.Outcome == "accepted");
        Assert.Contains("export-btn", accepted.Payload!);
    }

    [Fact]
    public async Task Rejecting_the_removal_tells_the_agent_to_restore_it_and_the_restored_task_passes()
    {
        var model = Model(
            Call("write_file", new { path = Page, content = WithButton }),
            Call("declare_done", new { summary = "restored" }), Say("done"));
        using var h = await HarnessAsync(model);
        Assert.Equal(TeamStatus.Blocked, (await RunAsync(h)).Status);

        var result = await RunAsync(h, Answer("B", "keep it"));

        Assert.Equal(TeamStatus.Passed, result.Status);
        Assert.Contains(model.Requests.SelectMany(r => r), m => m.Text.Contains("data-testid \"export-btn\""));
        Assert.DoesNotContain(h.Rows(LedgerKinds.Verify), e => e.Stage == "ui" && e.Outcome == "accepted");
    }

    [Fact]
    public async Task Without_a_ui_section_in_the_policy_nothing_is_guarded()
    {
        using var h = await TeamHarness.CreateAsync(Model(), new Limits(8, 3));

        Assert.Equal(TeamStatus.Passed, (await RunAsync(h)).Status);
    }

    [Fact]
    public async Task check_quality_shows_the_implementer_the_removal_before_verify_does()
    {
        var model = new ScriptedChatClient(
            Say("Approach: add QuoteStatus.cs"), Call("declare_done", new { summary = "verify block already fails" }), Say("done"),
            Call("write_file", new { path = NewFile, content = "public enum QuoteStatus { Draft }\n" }),
            Call("write_file", new { path = Page, content = WithoutButton }),
            Call("check_quality", new { }),
            Call("declare_done", new { summary = "added" }), Say("done"));
        using var h = await HarnessAsync(model);

        await RunAsync(h);

        Assert.Contains(model.Requests.SelectMany(r => r).SelectMany(m => m.Contents).OfType<Microsoft.Extensions.AI.FunctionResultContent>(),
            r => r.Result?.ToString()!.Contains("data-testid \"export-btn\" existed and is gone") == true);
    }
}
