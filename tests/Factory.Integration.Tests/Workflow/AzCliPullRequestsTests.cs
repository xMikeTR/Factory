using Factory.S2;
using Xunit;

namespace Factory.Integration.Tests.Workflow;

/// The az CLI path against a fake `az` that logs its arguments and answers like Azure DevOps would.
public class AzCliPullRequestsTests : IDisposable
{
    private const string Url = "https://dev.azure.com/org/proj/_git/repo/pullrequest/42";
    private readonly string _dir = Directory.CreateTempSubdirectory("azcli-").FullName;
    private string Log => Path.Combine(_dir, "calls.log");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// `active` lists answer `existing`; `completed` lists answer `merged`; create answers the url.
    private AzCliPullRequests Az(string existing = "", string merged = "")
    {
        var script = Path.Combine(_dir, "az");
        File.WriteAllText(script, $$"""
            #!/usr/bin/env bash
            printf '%s\x1f' "$@" >> '{{Log}}'; echo >> '{{Log}}'
            case "$*" in
              *"--status active"*) echo '{{existing}}' ;;
              *"--status completed"*) echo '{{merged}}' ;;
              *"pr create"*) echo '{{Url}}' ;;
            esac
            """.Replace("\r", ""));
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new AzCliPullRequests(_dir, script);
    }

    private List<string[]> Calls() => File.ReadAllLines(Log).Select(l => l.Split('\x1f', StringSplitOptions.RemoveEmptyEntries)).ToList();

    [Fact]
    public async Task Ensure_creates_a_pr_from_the_integration_branch_when_none_is_open_and_reuses_an_open_one()
    {
        Assert.Equal(Url, await Az().EnsurePullRequestAsync("factory/r1/integration", "main", "[factory] h: 2 tasks", "body", default));
        var create = Calls()[1];
        Assert.Equal(["repos", "pr", "create", "--source-branch", "factory/r1/integration", "--target-branch", "main", "--title", "[factory] h: 2 tasks", "--description", "body"],
            create.Take(11));

        File.Delete(Log);
        Assert.Equal(Url, await Az(existing: Url).EnsurePullRequestAsync("factory/r1/integration", "main", "t", "b", default));
        Assert.Single(Calls()); // only the list: nothing created
    }

    [Fact]
    public async Task Merged_is_the_completed_pr_from_the_branch_or_null()
    {
        Assert.Null(await Az().MergedPullRequestAsync("factory/r1/integration", default));
        Assert.Equal(Url, await Az(merged: Url).MergedPullRequestAsync("factory/r1/integration", default));
        Assert.All(Calls(), c => Assert.Equal(["repos", "pr", "list", "--source-branch", "factory/r1/integration", "--status", "completed"], c.Take(7)));
    }

    [Fact]
    public async Task Auto_merge_sets_auto_complete_with_squash_on_the_pr_id()
    {
        await Az().EnableAutoMergeAsync(Url, default);
        Assert.Equal(["repos", "pr", "update", "--id", "42", "--auto-complete", "true", "--squash", "true"], Calls().Single());
    }

    [Fact]
    public void A_body_over_the_azure_devops_limit_is_cut_with_a_note()
    {
        Assert.Equal("short", AzCliPullRequests.Description("short"));
        var cut = AzCliPullRequests.Description(new string('x', 10_000));
        Assert.Equal(AzCliPullRequests.MaxDescription, cut.Length);
        Assert.Contains("cut to fit Azure DevOps", cut);
    }

    [Fact]
    public void Github_and_azdo_together_are_refused()
    {
        Assert.Throws<ArgumentException>(() => CliOptions.PullRequestHost(new() { ["github"] = "", ["azdo"] = "" }, _dir));
        Assert.IsType<AzCliPullRequests>(CliOptions.PullRequestHost(new() { ["azdo"] = "" }, _dir));
        Assert.Null(CliOptions.PullRequestHost(new(), _dir));
    }
}
