using Factory.Core;
using Factory.Harness;
using Factory.Integration.Tests.Support;
using Factory.S2.Quality;
using Microsoft.Extensions.AI;
using Xunit;
using static Factory.Integration.Tests.Support.ScriptedChatClient;

namespace Factory.Integration.Tests.Quality;

using Core = Factory.Core.Core;

public class UxReviewRunnerTests
{
    // Writes one screenshot named by the convention and one file that does not follow it.
    private const string Command =
        "printf png > \"$FACTORY_SCREENSHOT_DIR/bookings-api__ready__desktop.png\" && printf x > \"$FACTORY_SCREENSHOT_DIR/stray.png\"";

    [Fact]
    public async Task Screenshots_are_kept_and_ledgered_and_without_the_judge_nothing_is_scored()
    {
        var model = new ScriptedChatClient();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3));

        var r = await UxReviewRunner.RunAsync(h.Services, h.Handover, "ux-1", Command, h.Repo, judge: false, default);

        var shot = Assert.Single(r.Shots);
        Assert.Equal(("bookings-api", "ready", "desktop"), (shot.ElementSlug, shot.State, shot.Breakpoint));
        Assert.Empty(r.Scores);
        Assert.Equal("taken", Assert.Single(h.Rows(LedgerKinds.UxScreenshots)).Outcome);
        Assert.Empty(h.Rows(LedgerKinds.UxJudge));
        Assert.Equal(0, model.Remaining);
    }

    [Fact]
    public async Task The_judge_ledgers_a_gradient_per_shot_and_never_blocks()
    {
        var model = new ScriptedChatClient();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3));
        model.Enqueue(
            Call("submit_ux_scores", new { hierarchy = 4, clarity = 3, consistency = 5, note = "none" }),
            Say("done"));

        var r = await UxReviewRunner.RunAsync(h.Services, h.Handover, "ux-2", Command, h.Repo, judge: true, default);

        var (_, score) = Assert.Single(r.Scores);
        Assert.Equal((4, 3, 5), (score.Hierarchy, score.Clarity, score.Consistency));
        var row = Assert.Single(h.Rows(LedgerKinds.UxJudge));
        Assert.Equal("gradient", row.Outcome);
        Assert.Equal("bookings-api", row.Element);
        Assert.Contains("bookings-api", UxReviewRunner.Render(r));
    }
}

/// The approved design beside the built page: the judge sees both, only when the approved picture is the frozen one.
public class UxReviewApprovedTests
{
    private static async Task<(TeamHarness H, ScriptedChatClient Model, Handover Handover, UiHandoverDir Ui, string Command)> SetupAsync()
    {
        var model = new ScriptedChatClient();
        var h = await TeamHarness.CreateAsync(model, new Limits(8, 3));
        var ui = UiHandoverDir.Create();
        var handover = Core.ParseHandover(await File.ReadAllTextAsync(await Factory.S2.HandoverInput.ResolveAsync(ui.Dir, Directory.CreateTempSubdirectory("ux-state").FullName, default)));
        var slug = UxReview.slug(handover.Ui.Head.Component);
        var command = $"printf built > \"$FACTORY_SCREENSHOT_DIR/{slug}__ready__desktop.png\"";
        model.Enqueue(Call("submit_ux_scores", new { hierarchy = 4, clarity = 3, consistency = 5, note = "none" }), Say("done"));
        return (h, model, handover, ui, command);
    }

    private static void Approve(TeamHarness h, UiHandoverDir ui, byte[]? bytes = null)
    {
        var dir = Path.Combine(h.Repo, "ui-contract", "previews", ui.Screen);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "ready-chromium-linux.png"), bytes ?? File.ReadAllBytes(ui.Preview("ready-chromium-linux.png")));
    }

    private static IReadOnlyList<AIContent> Sent(ScriptedChatClient model) => model.Requests[0].Last(m => m.Role == ChatRole.User).Contents.ToList();

    [Fact]
    public async Task The_judge_gets_the_intent_then_the_approved_design_then_the_built_page()
    {
        var (h, model, handover, ui, command) = await SetupAsync();
        using var _ = h;
        Approve(h, ui);

        await UxReviewRunner.RunAsync(h.Services, handover, "ux-a", command, h.Repo, judge: true, default);

        var c = Sent(model);
        Assert.Equal(5, c.Count);
        Assert.Contains("state `ready`", Assert.IsType<TextContent>(c[0]).Text);
        Assert.Equal("Approved design", Assert.IsType<TextContent>(c[1]).Text);
        Assert.Equal(File.ReadAllBytes(ui.Preview("ready-chromium-linux.png")), Assert.IsType<DataContent>(c[2]).Data.ToArray());
        Assert.Equal("Built page", Assert.IsType<TextContent>(c[3]).Text);
        Assert.Equal("built"u8.ToArray(), Assert.IsType<DataContent>(c[4]).Data.ToArray());
    }

    [Fact]
    public async Task A_tampered_approved_picture_is_not_shown_and_the_text_says_so()
    {
        var (h, model, handover, ui, command) = await SetupAsync();
        using var _ = h;
        Approve(h, ui, "tampered"u8.ToArray());

        await UxReviewRunner.RunAsync(h.Services, handover, "ux-b", command, h.Repo, judge: true, default);

        var c = Sent(model);
        Assert.Equal(2, c.Count);
        Assert.Contains("not the one the handover froze", Assert.IsType<TextContent>(c[0]).Text);
        Assert.Single(c.OfType<DataContent>());
    }

    [Fact]
    public async Task A_missing_approved_picture_gives_one_image_and_a_note()
    {
        var (h, model, handover, _, command) = await SetupAsync();
        using var __ = h;

        await UxReviewRunner.RunAsync(h.Services, handover, "ux-c", command, h.Repo, judge: true, default);

        var c = Sent(model);
        Assert.Contains("no approved picture", Assert.IsType<TextContent>(c[0]).Text);
        Assert.Single(c.OfType<DataContent>());
    }

    [Fact]
    public void The_rubric_asks_for_the_departure_from_the_approved_design()
    {
        Assert.Contains("When an approved design is shown, judge how far the built page departs from it.", UxReview.rubric);
    }
}
