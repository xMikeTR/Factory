using Factory.Core;
using Factory.Harness.Policy;
using Factory.Integration.Tests.Support;
using Factory.S2.Quality;
using Xunit;

namespace Factory.Integration.Tests.Quality;

public class QualityGateTests
{
    private static readonly QualityThresholds Limits3 = new(3, 30.0, 80);

    private const string Branchy = """
        class Rater
        {
            int Rate(int x)
            {
                if (x > 1) x++;
                if (x > 2) x++;
                if (x > 3) x++;
                return x;
            }
        }
        """;

    private static async Task<(TeamHarness H, string Wt)> Team()
    {
        var h = await TeamHarness.CreateAsync(new ScriptedChatClient(), new Limits(3, 2));
        return (h, await h.Services.Git.EnsureTeamAsync(TeamHarness.RunId, "team-x", CancellationToken.None));
    }

    [Fact]
    public async Task New_code_over_a_threshold_is_a_violation_naming_the_function()
    {
        var (h, wt) = await Team();
        using var _ = h;
        await File.WriteAllTextAsync(Path.Combine(wt, "src/Pricing.Data/Rater.cs"), Branchy);

        var violations = await QualityGate.EvaluateAsync(h.Services.Git, wt, TeamHarness.RunId, Limits3, CancellationToken.None);

        var v = Assert.Single(violations);
        Assert.Equal("Rater.Rate", v.Function.Name);
        Assert.Equal(Measure.Cyclomatic, v.Measure);
        Assert.Equal(4.0, v.Value);
    }

    [Fact]
    public async Task Code_the_task_did_not_touch_is_not_judged()
    {
        var (h, wt) = await Team();
        using var _ = h;
        await File.WriteAllTextAsync(Path.Combine(wt, "src/Pricing.Data/Rater.cs"), Branchy);
        await h.Services.Git.CommitAllAsync(wt, "debt", CancellationToken.None);
        // the integration branch does not have it, so it is "new" until it is in the base; move the base
        await h.GitIn(h.Services.Git.IntegrationPath(TeamHarness.RunId), "merge", "--ff-only", GitWorkspaceBranch());
        await File.WriteAllTextAsync(Path.Combine(wt, "src/Pricing.Data/Other.cs"), "class Other { void F() { } }\n");

        var violations = await QualityGate.EvaluateAsync(h.Services.Git, wt, TeamHarness.RunId, Limits3, CancellationToken.None);

        Assert.Empty(violations);
    }

    private static string GitWorkspaceBranch() => Factory.S2.Git.GitWorkspace.TeamBranch(TeamHarness.RunId, "team-x");

    [Fact]
    public async Task Only_csharp_files_are_measured()
    {
        var (h, wt) = await Team();
        using var _ = h;
        await File.WriteAllTextAsync(Path.Combine(wt, "src/Pricing.Data/notes.md"), Branchy);

        Assert.Empty(await QualityGate.EvaluateAsync(h.Services.Git, wt, TeamHarness.RunId, Limits3, CancellationToken.None));
    }
}

public class FitnessGateTests
{
    private const string RefsRating = """
        <Project Sdk="Microsoft.NET.Sdk">
          <ItemGroup><ProjectReference Include="..\Rating.Engine\Rating.Engine.csproj" /></ItemGroup>
        </Project>
        """;

    [Fact]
    public void A_project_reference_is_an_edge_between_repo_relative_projects()
    {
        var edges = CsprojEdges.Read("src/Pricing.Data/Pricing.Data.csproj", RefsRating);

        var e = Assert.Single(edges);
        Assert.Equal(("src/Pricing.Data/Pricing.Data.csproj", "src/Rating.Engine/Rating.Engine.csproj"), (e.Source, e.Target));
    }

    [Fact]
    public async Task A_new_reference_the_model_does_not_allow_is_a_violation_and_one_it_allows_is_not()
    {
        using var h = await TeamHarness.CreateAsync(new ScriptedChatClient(), new Limits(3, 2));
        var wt = await h.Services.Git.EnsureTeamAsync(TeamHarness.RunId, "team-x", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(wt, "src/Pricing.Data/Pricing.Data.csproj"), RefsRating);

        var violations = await QualityGate.FitnessAsync(h.Services.Git, wt, TeamHarness.RunId, h.Handover, CancellationToken.None);

        var v = Assert.Single(violations);
        Assert.Equal("Pricing Platform/Quotes DB", v.FromElement.Value);
        Assert.Equal("Pricing Platform/Rating Engine", v.ToElement.Value);
    }
}
