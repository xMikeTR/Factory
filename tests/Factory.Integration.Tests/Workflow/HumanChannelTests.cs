using Factory.S2;
using Xunit;

namespace Factory.Integration.Tests.Workflow;

public class HumanChannelTests
{
    [Theory]
    [InlineData("merged", true, null)]
    [InlineData("  Merged  Ana Lopez ", true, "Ana Lopez")]
    [InlineData("mergedd", false, null)]
    [InlineData("merged-not-yet", false, null)]
    [InlineData("", false, null)]
    [InlineData("no", false, null)]
    public void Only_the_word_merged_approves(string typed, bool approved, string? by)
    {
        var r = ConsoleHumanChannel.Answer("run-1", typed);

        Assert.Equal((approved, by), (r.Merged, r.ApprovedBy));
    }

    [Fact]
    public void A_pull_request_comment_with_no_author_or_body_is_kept_empty_not_a_crash()
    {
        var comments = GhCliPullRequests.ParseComments("""{"comments":[{"author":null,"body":"/approve ok"},{"author":{"login":"ana"}},{"author":{"login":"bo"},"body":"/verified x"}]}""");

        Assert.Equal([("", "/approve ok"), ("ana", ""), ("bo", "/verified x")], comments.Select(c => (c.Author, c.Body)));
    }
}
