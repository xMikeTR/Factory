using Factory.Core;
using CoreHelpers = Factory.Core.Core;
using Factory.Harness.Sandbox;
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

    [Fact]
    public async Task The_environment_lives_for_the_run_and_its_variables_reach_the_tests()
    {
        var model = new ScriptedChatClient(
            Call("submit_qa_tests", new { tests = new[] { Test("env", "new", "test \"$QA_SQL_HOST\" = sql") } }), Say("done"));
        var (h, _, integration) = await SetUpAsync(model);
        using var _h = h;
        var stopped = 0;
        var runner = new AgentQaRunner(h.Services.Agents, h.Services.Git, h.Services.Sandboxes, "main", _ => [Req], ["ls"], TimeSpan.FromSeconds(30),
            environment: _ => Task.FromResult(new QaEnvironment(h.Services.Sandboxes, new Dictionary<string, string> { ["QA_SQL_HOST"] = "sql" }, () => { stopped++; return Task.CompletedTask; })));

        var run = await runner.RunAsync(TeamHarness.RunId, integration, default);

        var test = Assert.Single(run.Tests);
        Assert.Equal(QaOutcome.QaPassed, test.OnIntegrated);
        Assert.Equal(1, stopped);
    }

    [Fact]
    public async Task A_follow_up_run_measures_the_tests_the_author_left_without_asking_the_author_again()
    {
        var model = new ScriptedChatClient(
            Call("write_file", new { path = "tests/qa/status.sh", content = "test -f src/Pricing.Data/QuoteStatus.cs\n" }),
            Call("submit_qa_tests", new { tests = new[] { Test("status", "new", "sh tests/qa/status.sh") } }), Say("done"));
        var (h, runner, integration) = await SetUpAsync(model);
        using var _ = h;
        await runner.RunAsync(TeamHarness.RunId, integration, default);
        var requests = model.Requests.Count;

        var again = await runner.RerunAsync(TeamHarness.RunId, integration, default);

        Assert.Equal(requests, model.Requests.Count);
        Assert.Equal([Req], again.Requirements);
        Assert.Equal([("status", QaKind.New, QaOutcome.QaFailed, QaOutcome.QaPassed)], again.Tests.Select(t => (t.Id, t.Kind, t.OnBaseline, t.OnIntegrated)));
    }

    [Fact]
    public async Task A_follow_up_run_with_no_manifest_has_nothing_to_measure()
    {
        var (h, runner, integration) = await SetUpAsync(new ScriptedChatClient());
        using var _ = h;

        var again = await runner.RerunAsync(TeamHarness.RunId, integration, default);

        Assert.Empty(again.Tests);
        Assert.False(again.TimedOut);
    }

    [Fact]
    public async Task A_follow_up_run_with_an_unreadable_manifest_has_nothing_to_measure()
    {
        var (h, runner, integration) = await SetUpAsync(new ScriptedChatClient());
        using var _ = h;
        Directory.CreateDirectory(Path.Combine(integration, AgentQaRunner.TestDir));
        await File.WriteAllTextAsync(Path.Combine(integration, AgentQaRunner.TestDir, AgentQaRunner.ManifestFile), "{ not json");

        var again = await runner.RerunAsync(TeamHarness.RunId, integration, default);

        Assert.Empty(again.Tests);
    }
}
