using Factory.Core;
using Factory.Harness.Policy;
using Factory.Integration.Tests.Support;
using Factory.S2.Quality;
using Xunit;

namespace Factory.Integration.Tests.Quality;

public class UiGateTests
{
    private static readonly UiPolicy Defaults = new([], []);
    private static readonly UiItem Export = new(UiItemKind.TestId, "export-btn");

    /// A team worktree whose integration branch already has a page with an export button.
    private static async Task<(TeamHarness H, string Wt)> TeamWithPage()
    {
        var h = await TeamHarness.CreateAsync(new ScriptedChatClient(), new Limits(3, 2));
        var wt = await h.Services.Git.EnsureTeamAsync(TeamHarness.RunId, "team-x", CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(wt, "web"));
        await File.WriteAllTextAsync(Path.Combine(wt, "web/Page.tsx"), "export const P = () => <button data-testid=\"export-btn\" />;\n");
        await h.Services.Git.CommitAllAsync(wt, "page", CancellationToken.None);
        await h.GitIn(h.Services.Git.IntegrationPath(TeamHarness.RunId), "merge", "--ff-only", Factory.S2.Git.GitWorkspace.TeamBranch(TeamHarness.RunId, "team-x"));
        return (h, wt);
    }

    [Fact]
    public async Task A_feature_the_task_deleted_is_a_violation_even_when_the_file_went_with_it()
    {
        var (h, wt) = await TeamWithPage();
        using var _ = h;
        File.Delete(Path.Combine(wt, "web/Page.tsx"));

        var v = await UiGate.CheckAsync(h.Services.Git, wt, TeamHarness.RunId, Defaults, [], [], CancellationToken.None);

        Assert.Equal([UiViolation.NewRemoved(Export)], v);
    }

    [Fact]
    public async Task An_untouched_ui_and_an_authorised_removal_are_clean()
    {
        var (h, wt) = await TeamWithPage();
        using var _ = h;
        Assert.Empty(await UiGate.CheckAsync(h.Services.Git, wt, TeamHarness.RunId, Defaults, [], [], CancellationToken.None));

        File.Delete(Path.Combine(wt, "web/Page.tsx"));
        Assert.Empty(await UiGate.CheckAsync(h.Services.Git, wt, TeamHarness.RunId, Defaults, [], [Export], CancellationToken.None));
    }

    [Fact]
    public async Task A_contract_item_nobody_ever_built_is_missing()
    {
        var (h, wt) = await TeamWithPage();
        using var _ = h;
        var reports = new UiItem(UiItemKind.Route, "/reports");

        var v = await UiGate.CheckAsync(h.Services.Git, wt, TeamHarness.RunId, Defaults, [reports], [], CancellationToken.None);

        Assert.Equal([UiViolation.NewContractMissing(reports)], v);
        Assert.Equal("- route \"/reports\" is in .factory/ui-contract.yaml and is missing", UiGate.Line(v[0]));
    }
}
