using System.Net;
using System.Text;
using Factory.Core;
using Factory.Harness.Registry;
using Xunit;

namespace Factory.Integration.Tests.Registry;

/// No live call: a stub handler stands in for Jira.
public class JiraRestClientTests
{
    private sealed class Stub(Func<HttpRequestMessage, string?, (HttpStatusCode, string)> answer) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Body, string? Auth)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method, request.RequestUri!.PathAndQuery, body, request.Headers.Authorization?.ToString()));
            var (code, json) = answer(request, body);
            return new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private const string Transitions = """{"transitions":[{"id":"11","to":{"name":"In Progress"}},{"id":"31","to":{"name":"Done"}}]}""";

    private static Stub Jira() => new((r, _) => r.Method == HttpMethod.Get
        ? (HttpStatusCode.OK, r.RequestUri!.AbsolutePath.EndsWith("/transitions") ? Transitions : """{"fields":{"status":{"name":"To Do"}}}""")
        : (HttpStatusCode.NoContent, ""));

    private static JiraRestClient Client(Stub s, string? user = "me@x.com") =>
        new(new HttpClient(s), "https://x.atlassian.net/", user, "s3cret-token");

    [Fact]
    public async Task Status_is_read_with_basic_auth_for_cloud_and_a_bearer_pat_without_a_user()
    {
        var cloud = Jira();
        Assert.Equal("To Do", await Client(cloud).StatusAsync("REQ-1", default));
        Assert.StartsWith("Basic ", cloud.Calls[0].Auth);

        var pat = Jira();
        await Client(pat, user: null).StatusAsync("REQ-1", default);
        Assert.Equal("Bearer s3cret-token", pat.Calls[0].Auth);
    }

    [Fact]
    public async Task An_unknown_issue_has_no_status()
    {
        var s = new Stub((_, _) => (HttpStatusCode.NotFound, "{}"));
        Assert.Null(await Client(s).StatusAsync("NOPE-1", default));
    }

    [Fact]
    public async Task A_transition_posts_the_id_of_the_workflow_step_that_ends_in_the_target_status()
    {
        var s = Jira();

        Assert.Null(await Client(s).TransitionAsync("REQ-1", "in progress", default));

        var post = Assert.Single(s.Calls, c => c.Method == HttpMethod.Post);
        Assert.Equal("/rest/api/3/issue/REQ-1/transitions", post.Path);
        Assert.Contains("\"id\":\"11\"", post.Body);
    }

    [Fact]
    public async Task A_status_the_workflow_cannot_reach_is_reported_and_nothing_is_posted()
    {
        var s = Jira();

        var why = await Client(s).TransitionAsync("REQ-1", "Shipped", default);

        Assert.Contains("no transition to 'Shipped'", why);
        Assert.DoesNotContain(s.Calls, c => c.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Apply_moves_planned_transitions_and_a_failure_message_never_carries_the_token()
    {
        var s = new Stub((r, _) => r.Method == HttpMethod.Get ? (HttpStatusCode.OK, Transitions) : (HttpStatusCode.Forbidden, "{}"));
        var plan = new[] { MirrorAction.NewTransition("REQ-1", "W-1", "To Do", "In Progress") };

        var (moved, failed) = await JiraMirrorReport.ApplyAsync(Client(s), plan, default);

        Assert.Empty(moved);
        Assert.Single(failed);
        Assert.DoesNotContain("s3cret-token", failed[0]);
    }
}
