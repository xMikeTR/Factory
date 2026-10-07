using Factory.Harness.Sandbox;
using Xunit;

namespace Factory.Integration.Tests.Verify;

/// PObserve over test logs, as the integration verify step runs it: `pobserve-check` inside the
/// runner image (opt-in: FACTORY_TEST_DOCKER=1, image built from docker/runner) against the
/// fixture handover's P spec. Log lines follow the logging contract, `pobserve <event> <payload>`.
[Trait("needs", "docker")]
public sealed class PObserveTests : IDisposable
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("FACTORY_TEST_DOCKER") == "1";
    private readonly string _worktree = Directory.CreateTempSubdirectory("pobserve-").FullName;

    public PObserveTests()
    {
        var behaviour = Directory.CreateDirectory(Path.Combine(_worktree, "behaviour")).FullName;
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures/handover/behaviour/quotes-writes.p"), Path.Combine(behaviour, "quotes-writes.p"));
    }

    private Task<Factory.Harness.ProcessResult> CheckAsync(params string[] log) => RunCheckAsync("", log);

    private async Task<Factory.Harness.ProcessResult> RunCheckAsync(string flags, string[] log)
    {
        var logs = Directory.CreateDirectory(Path.Combine(_worktree, "logs")).FullName;
        await File.WriteAllLinesAsync(Path.Combine(logs, "test.log"), log);
        await using var sandbox = await DockerSandbox.StartAsync("pobserve", _worktree, new DockerSandboxOptions(RestoreOnStart: false), default);
        return await sandbox.RunAsync(string.Join(" ", new[] { "pobserve-check", flags, "behaviour NoLostUpdate logs" }.Where(s => s != "")), _worktree, TimeSpan.FromMinutes(5),
            new Dictionary<string, string>(), default);
    }

    [Fact]
    public async Task A_log_that_honours_the_spec_passes()
    {
        if (!Enabled) return; // opt-in

        var r = await CheckAsync(
            "12:00:01 INFO quotes: write accepted",
            "12:00:01 INFO pobserve eCommitted 1",
            "12:00:02 INFO pobserve eCommitted 2",
            "12:00:03 DEBUG unrelated line",
            "12:00:03 INFO pobserve eCommitted 3");

        Assert.True(r.ExitCode == 0, r.Output);
        Assert.Contains("3 events", r.Output);
    }

    [Fact]
    public async Task A_lost_update_in_the_log_fails_with_the_specs_assertion()
    {
        if (!Enabled) return; // opt-in

        var r = await CheckAsync(
            "12:00:01 INFO pobserve eCommitted 1",
            "12:00:02 INFO pobserve eCommitted 1");

        Assert.True(r.ExitCode == 2, r.Output); // PObserve: assertion
        Assert.Contains("lost update: 2 commits, value 1", r.Output);
    }

    /// live4: five tests each committed their own quote at version 1; one stream read that as lost updates.
    [Fact]
    public async Task Each_key_is_checked_as_its_own_history()
    {
        if (!Enabled) return; // opt-in

        var r = await CheckAsync(
            "12:00:01 INFO pobserve eCommitted 1 key=Q-1",
            "12:00:01 INFO pobserve eCommitted 1 key=Q-2",
            "12:00:02 INFO pobserve eCommitted 2 key=Q-1",
            "12:00:02 INFO pobserve eCommitted 2 key=Q-2");

        Assert.True(r.ExitCode == 0, r.Output);
        Assert.Contains("4 events", r.Output);
    }

    [Fact]
    public async Task A_lost_update_within_one_key_still_fails()
    {
        if (!Enabled) return; // opt-in

        var r = await CheckAsync(
            "12:00:01 INFO pobserve eCommitted 1 key=Q-1",
            "12:00:01 INFO pobserve eCommitted 1 key=Q-2",
            "12:00:02 INFO pobserve eCommitted 1 key=Q-1");

        Assert.True(r.ExitCode == 2, r.Output);
        Assert.Contains("lost update: 2 commits, value 1", r.Output);
    }

    [Fact]
    public async Task A_garbled_event_line_is_a_parse_error_not_a_pass()
    {
        if (!Enabled) return; // opt-in

        var r = await CheckAsync("12:00:01 INFO pobserve eCommitted one");

        Assert.True(r.ExitCode == 4, r.Output); // PObserve: parse error
        Assert.Contains("pobserve eCommitted one", r.Output);
    }

    /// live5: a log with no marked lines is a test that logged nothing, not a spec that held.
    [Fact]
    public async Task A_log_without_events_fails_instead_of_passing_vacuously()
    {
        if (!Enabled) return; // opt-in

        var r = await CheckAsync("12:00:01 INFO quotes: write accepted", "12:00:02 INFO {\"event\":\"eCommitted\",\"rev\":1}");

        Assert.True(r.ExitCode == 3, r.Output);
        Assert.Contains("no events", r.Output);
    }

    /// live5: `! pobserve-check` turned a spec that did not compile into a passing negative check.
    [Theory]
    [InlineData(new[] { "INFO pobserve eCommitted 1", "INFO pobserve eCommitted 1" }, 0)] // the violation it expects
    [InlineData(new[] { "INFO pobserve eCommitted 1", "INFO pobserve eCommitted 2" }, 1)] // the spec held
    [InlineData(new[] { "INFO pobserve eCommitted one" }, 4)]                             // garbled, not a violation
    public async Task Expect_violation_passes_only_on_the_specs_assertion(string[] log, int exit)
    {
        if (!Enabled) return; // opt-in

        var r = await RunCheckAsync("--expect-violation", log);

        Assert.True(r.ExitCode == exit, r.Output);
    }

    [Fact]
    public async Task Marked_events_the_spec_does_not_declare_are_named_in_the_output()
    {
        if (!Enabled) return; // opt-in

        var r = await CheckAsync("INFO pobserve eCommitted 1", "INFO pobserve eCommited 2");

        Assert.True(r.ExitCode == 0, r.Output);
        Assert.Contains("not declared in the spec: eCommited", r.Output);
    }

    public void Dispose()
    {
        try { Directory.Delete(_worktree, recursive: true); } catch (IOException) { }
    }
}
