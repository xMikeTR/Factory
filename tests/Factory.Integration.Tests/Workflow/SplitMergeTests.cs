using Factory.Core;
using Factory.Harness;
using Factory.S2;
using Factory.S2.Git;
using Xunit;

namespace Factory.Integration.Tests.Workflow;

/// The merge step of a split run: one PR per repo from that repo's integration branch, never merged by the factory.
public sealed class SplitMergeTests
{
    private sealed class RecordingHost(string name) : IPullRequests
    {
        public List<(string Head, string Base, string Title, string Body)> Opened { get; } = [];
        public int AutoMerges { get; private set; }
        public Task<string> EnsurePullRequestAsync(string head, string baseBranch, string title, string body, CancellationToken ct)
        {
            Opened.Add((head, baseBranch, title, body));
            return Task.FromResult($"https://host/{name}/pull/1");
        }
        public Task EnableAutoMergeAsync(string url, CancellationToken ct) { AutoMerges++; return Task.CompletedTask; }
    }

    [Fact]
    public void Api_comes_first_whatever_order_the_repos_were_named()
    {
        Assert.Equal(["Api", "Ui"], SplitMerge.Order(["Ui", "Api"]));
        Assert.Equal(["Api", "Web", "Ui"], SplitMerge.Order(["Web", "Api", "Ui"]));
        Assert.Equal(["Ui", "Web"], SplitMerge.Order(["Ui", "Web"]));
    }

    [Fact]
    public async Task One_PR_per_repo_from_its_own_integration_branch_Api_first_and_the_factory_never_merges()
    {
        var api = new RecordingHost("api");
        var ui = new RecordingHost("ui");
        var prs = await SplitMerge.OpenAsync(["Ui", "Api"], "r1", "main",
            repo => repo == "Api" ? api : ui, repo => $"title {repo}", repo => $"body {repo}", CancellationToken.None);

        Assert.Equal(["Api", "Ui"], prs.Select(p => p.Repo));
        Assert.Equal(("factory/r1/integration", "main", "title Api", "body Api"), Assert.Single(api.Opened));
        Assert.Equal(("factory/r1/integration", "main", "title Ui", "body Ui"), Assert.Single(ui.Opened));
        Assert.Equal(0, api.AutoMerges + ui.AutoMerges);

        var done = SplitMerge.Finished("r1", prs);
        Assert.Equal("ready_for_review", done.Status);
        Assert.Equal("Api: https://host/api/pull/1\nUi: https://host/ui/pull/1", done.Detail);
    }

    [Fact]
    public void The_PR_body_states_the_merge_order()
    {
        var note = SplitMerge.MergeOrderNote(["Api", "Ui"], "r1");
        Assert.Contains("Merge the Api PR first, then the Ui PR", note);
        Assert.Contains("complete only when both are merged", note);
        Assert.Contains("factory-s2 close --run-id r1", note);
    }
}
