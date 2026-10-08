using Factory.Core;
using CoreHelpers = Factory.Core.Core;
using Factory.Integration.Tests.Support;
using Factory.S2.Quality;
using Xunit;
using static Factory.Integration.Tests.Support.ScriptedChatClient;

namespace Factory.Integration.Tests.Quality;

public class AgentQaRunnerTests
{
    private const string Req = "T-001: quote status";

    private static object Test(string id, string kind, string command) => new { id, requirement = Req, kind, command };

    private static async Task<(TeamHarness H, AgentQaRunner Runner, string Integration)> SetUpAsync(ScriptedChatClient model)
    {
        var h = await TeamHarness.CreateAsync(model, new Limits(4, 3));
        var integration = h.Services.Git.IntegrationPath(TeamHarness.RunId);
        await File.WriteAllTextAsync(Path.Combine(integration, "src/Pricing.Data/QuoteStatus.cs"), "enum QuoteStatus {}\n");
        await h.Services.Git.CommitAllAsync(integration, "the run's work", default); // integrated = base + QuoteStatus.cs
        var runner = new AgentQaRunner(h.Services.Agents, h.Services.Git, h.Services.Sandboxes, "main", _ => [Req], ["ls"], TimeSpan.FromSeconds(30));
        return (h, runner, integration);
    }

    [Fact]
    public async Task A_new_test_red_on_the_base_branch_and_green_on_the_integrated_result_is_judged_as_such()
    {
        var model = new ScriptedChatClient(
            Call("write_file", new { path = "tests/qa/status.sh", content = "test -f src/Pricing.Data/QuoteStatus.cs\n" }),
            Call("submit_qa_tests", new { tests = new[] { Test("status", "new", "sh tests/qa/status.sh"), Test("quote", "characterisation", "test -f src/Pricing.Data/Quote.cs") } }),
            Say("done"));
        var (h, runner, integration) = await SetUpAsync(model);
        using var _ = h;

        var run = await runner.RunAsync(TeamHarness.RunId, integration, default);

        Assert.False(run.TimedOut);
        Assert.Equal([Req], run.Requirements);
        Assert.Equal([("status", QaKind.New, QaOutcome.QaFailed, QaOutcome.QaPassed), ("quote", QaKind.Characterisation, QaOutcome.QaPassed, QaOutcome.QaPassed)],
            run.Tests.Select(t => (t.Id, t.Kind, t.OnBaseline, t.OnIntegrated)));
        Assert.Empty(Qa.judge(CoreHelpers.ToList(run.Requirements), CoreHelpers.ToList(run.Tests)));
    }

    [Fact]
    public async Task The_author_writes_only_under_tests_qa()
    {
        var model = new ScriptedChatClient(
            Call("write_file", new { path = "src/Pricing.Data/Quote.cs", content = "// tampered\n" }),
            Call("submit_qa_tests", new { tests = Array.Empty<object>() }), Say("done"));
        var (h, runner, integration) = await SetUpAsync(model);
        using var _ = h;

        await runner.RunAsync(TeamHarness.RunId, integration, default);

        Assert.Equal("// quote\n", await File.ReadAllTextAsync(Path.Combine(integration, "src/Pricing.Data/Quote.cs")));
    }

    [Fact]
    public async Task A_test_that_does_not_finish_is_a_timeout_not_a_failure()
    {
        var model = new ScriptedChatClient(
            Call("submit_qa_tests", new { tests = new[] { Test("slow", "new", "sleep 5") } }), Say("done"));
        var (h, _, integration) = await SetUpAsync(model);
        using var _h = h;
        var runner = new AgentQaRunner(h.Services.Agents, h.Services.Git, h.Services.Sandboxes, "main", _ => [Req], ["ls"], TimeSpan.FromMilliseconds(300));

        var run = await runner.RunAsync(TeamHarness.RunId, integration, default);

        Assert.True(run.TimedOut);
    }

    [Fact]
    public async Task No_declared_tests_leave_every_requirement_uncovered()
    {
        var model = new ScriptedChatClient(Call("submit_qa_tests", new { tests = Array.Empty<object>() }), Say("done"));
        var (h, runner, integration) = await SetUpAsync(model);
        using var _ = h;

        var run = await runner.RunAsync(TeamHarness.RunId, integration, default);

        Assert.Equal(QaFinding.NewUncovered(Req).ToString(), Assert.Single(Qa.judge(CoreHelpers.ToList(run.Requirements), CoreHelpers.ToList(run.Tests))).ToString());
    }
}
