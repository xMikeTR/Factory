using Factory.Core;
using Factory.Harness.Registry;
using Xunit;

namespace Factory.Integration.Tests.Registry;

public class JiraMirrorReportTests
{
    // REQ-1 is a Jira-style key, so W-1 mirrors to the issue REQ-1.
    private static string Registry(string ledger)
    {
        var root = Path.Combine(Path.GetTempPath(), "s2-jira-" + Guid.NewGuid().ToString("N"));
        void Put(string rel, string text)
        {
            var p = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }
        Put("requirements/REQ-1.yaml", "id: REQ-1\nelements:\n  - Pricing/Form\n");
        Put("work/W-1.yaml", "id: W-1\nrequirement: REQ-1\nelements: [Pricing/Form]\nwrites: [src/Form/**]\ndepends_on: []\ndone_when:\n  - gate: dotnet test\n");
        Put("ledger/designc.jsonl", ledger);
        return root;
    }

    private const string Pulled =
        """{"kind":"handover","handover":"h1","revision":"r1","item":"W-1","ts":1,"by":"mike"}""" + "\n" +
        """{"kind":"item_pulled","item":"W-1","ts":2,"by":"mike"}""" + "\n";

    private static Task<IReadOnlyList<MirrorAction>> Plan(string root, string jira, string mirrored) =>
        JiraMirrorReport.PlanAsync(root, null, "main",
            new Dictionary<string, string> { ["REQ-1"] = jira },
            mirrored.Length == 0 ? new Dictionary<string, string>() : new Dictionary<string, string> { ["REQ-1"] = mirrored },
            default);

    [Fact]
    public async Task An_active_item_moves_a_to_do_issue_to_in_progress()
    {
        var plan = await Plan(Registry(Pulled), "To Do", "To Do");

        var t = Assert.IsType<MirrorAction.Transition>(Assert.Single(plan));
        Assert.Equal(("REQ-1", "In Progress"), (t.key, t.target));
    }

    [Fact]
    public async Task A_status_edited_in_Jira_is_flagged_once_and_never_planned_as_a_transition()
    {
        var root = Registry(Pulled);
        var plan = await Plan(root, "Done", "To Do");
        Assert.IsType<MirrorAction.Drift>(Assert.Single(plan));

        Assert.Equal(1, await JiraMirrorReport.FlagDriftAsync(root, plan, DateTimeOffset.UtcNow, default));
        await JiraMirrorReport.FlagDriftAsync(root, plan, DateTimeOffset.UtcNow.AddMinutes(5), default);

        var rows = File.ReadAllLines(Path.Combine(root, "ledger", "pipeline.jsonl"));
        var row = Assert.Single(rows, r => r.Contains("item_awaiting_human"));
        Assert.Contains("Jira REQ-1 says", row);
        Assert.Contains("In Progress", row);
    }

    private static string Evidence() =>
        Pulled +
        """{"kind":"item_gate","item":"W-1","gate":"dotnet test","outcome":"fail","commit":"aaa1111","dirty":false,"ts":3}""" + "\n" +
        """{"kind":"item_gate","item":"W-1","gate":"dotnet test","outcome":"pass","commit":"bbb2222","dirty":false,"ts":4}""" + "\n" +
        """{"kind":"item_gate","item":"W-1","gate":"lint","outcome":"pass","commit":"bbb2222","dirty":true,"ts":5}""" + "\n" +
        """{"kind":"item_done","item":"W-1","commit":"bbb2222","evidence":["gate:dotnet test@bbb2222"],"ts":6,"by":"mike"}""" + "\n";

    [Fact]
    public async Task The_comment_names_the_item_the_run_the_commit_and_the_latest_result_of_each_gate_dirty_passes_marked()
    {
        var text = await JiraMirrorReport.EvidenceAsync(Registry(Evidence()), "W-1", "Done", default);

        Assert.Contains("W-1", text);
        Assert.Contains("Done", text);
        Assert.Contains("commit bbb2222", text);
        Assert.Contains("gate `dotnet test`: pass at bbb2222", text);
        Assert.DoesNotContain("aaa1111", text);
        Assert.Contains("gate `lint`: pass at bbb2222 (uncommitted changes: not evidence)", text);
    }

    [Fact]
    public async Task Apply_with_a_comment_posts_it_after_the_transition_and_a_failed_comment_does_not_undo_the_move()
    {
        var calls = new List<string>();
        var handler = new RecordingHandler(calls, failComment: true);
        var client = new JiraRestClient(new HttpClient(handler), "https://x.atlassian.net/", "me@x.com", "s3cret-token");
        var plan = new[] { MirrorAction.NewTransition("REQ-1", "W-1", "In Progress", "Done") };

        var (moved, failed) = await JiraMirrorReport.ApplyAsync(client, plan, default, (_, _) => Task.FromResult("evidence"));

        Assert.Equal("Done", moved["REQ-1"]);
        Assert.Single(failed);
        Assert.Contains("comment", failed[0]);
        Assert.DoesNotContain("s3cret-token", failed[0]);
        Assert.Equal(["GET /rest/api/3/issue/REQ-1/transitions", "POST /rest/api/3/issue/REQ-1/transitions", "POST /rest/api/3/issue/REQ-1/comment"], calls);
    }

    [Fact]
    public async Task Apply_without_a_comment_posts_none()
    {
        var calls = new List<string>();
        var client = new JiraRestClient(new HttpClient(new RecordingHandler(calls, failComment: false)), "https://x.atlassian.net/", "me@x.com", "t");

        await JiraMirrorReport.ApplyAsync(client, [MirrorAction.NewTransition("REQ-1", "W-1", "In Progress", "Done")], default);

        Assert.DoesNotContain(calls, c => c.EndsWith("/comment"));
    }

    private sealed class RecordingHandler(List<string> calls, bool failComment) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            calls.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            var comment = request.RequestUri.AbsolutePath.EndsWith("/comment");
            var code = comment && failComment ? System.Net.HttpStatusCode.Forbidden : System.Net.HttpStatusCode.OK;
            var json = request.Method == HttpMethod.Get ? """{"transitions":[{"id":"31","to":{"name":"Done"}}]}""" : "{}";
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(json) });
        }
    }
}
