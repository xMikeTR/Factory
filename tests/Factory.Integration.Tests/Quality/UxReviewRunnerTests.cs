using Factory.Core;
using Factory.Harness;
using Factory.Integration.Tests.Support;
using Factory.S2.Quality;
using Xunit;
using static Factory.Integration.Tests.Support.ScriptedChatClient;

namespace Factory.Integration.Tests.Quality;

public class UxReviewRunnerTests
{
    // Writes one screenshot named by the convention and one file that does not follow it.
    private const string Command =
        "printf png > \"$FACTORY_SCREENSHOT_DIR/quotes-api__ready__desktop.png\" && printf x > \"$FACTORY_SCREENSHOT_DIR/stray.png\"";

    [Fact]
    public async Task Screenshots_are_kept_and_ledgered_and_without_the_judge_nothing_is_scored()
    {
        var model = new ScriptedChatClient();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3));

        var r = await UxReviewRunner.RunAsync(h.Services, h.Handover, "ux-1", Command, h.Repo, judge: false, default);

        var shot = Assert.Single(r.Shots);
        Assert.Equal(("quotes-api", "ready", "desktop"), (shot.ElementSlug, shot.State, shot.Breakpoint));
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
        Assert.Equal("quotes-api", row.Element);
        Assert.Contains("quotes-api", UxReviewRunner.Render(r));
    }
}
