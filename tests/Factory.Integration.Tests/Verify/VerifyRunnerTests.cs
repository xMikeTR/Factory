using Factory.Harness;
using Factory.Harness.Sandbox;
using Factory.Integration.Tests.Support;
using Factory.S2.Git;
using Factory.S2.Verify;
using Xunit;
using static Factory.Integration.Tests.Support.Plans;

namespace Factory.Integration.Tests.Verify;

public class VerifyRunnerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("s2-verify-").FullName;
    /// Checks leave the worktree as they found it, so what a test wants to inspect afterwards
    /// is written here, outside it.
    private readonly string _out = Directory.CreateTempSubdirectory("s2-verify-out-").FullName;
    private readonly MemoryLedger _ledger = new();
    private VerifyRunner Runner => new(_ledger, new LocalSandboxFactory(), new GitWorkspace(_dir, Path.Combine(_out, "state")));

    public VerifyRunnerTests()
    {
        foreach (var args in new[] { new[] { "init", "-q" }, ["-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "--allow-empty", "-m", "init"] })
            if (ProcessRunner.RunAsync("git", args, _dir, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult().ExitCode != 0)
                throw new InvalidOperationException("git " + string.Join(' ', args));
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        Directory.Delete(_out, recursive: true);
    }

    [Fact]
    public async Task Each_check_starts_from_the_tree_as_found()
    {
        var outcomes = await Runner.RunTaskAsync("run-1", "team-T-001", Task("T-001",
            Auto("generates", "mkdir -p gen/obj && echo x > gen/obj/A.cs && echo y > log.txt"),
            Auto("sees nothing", "test ! -e gen && test ! -e log.txt")), 1, _dir, default);

        Assert.Equal([("generates", 0), ("sees nothing", 0)], outcomes.Select(o => (o.Name, o.ExitCode)));
    }

    [Fact]
    public async Task Cleanup_runs_even_when_the_check_fails()
    {
        var outcomes = await Runner.RunTaskAsync("run-1", "team-T-001", Task("T-001",
            Auto("seeded", "touch seeded; exit 3", cleanup: $"test -e seeded && rm seeded && touch {_out}/cleaned")), 1, _dir, default);

        var o = Assert.Single(outcomes);
        Assert.Equal(("seeded", 3), (o.Name, o.ExitCode));
        Assert.True(File.Exists(Path.Combine(_out, "cleaned")));
        Assert.False(File.Exists(Path.Combine(_dir, "seeded")));
    }

    [Fact]
    public async Task A_failed_cleanup_is_a_failed_outcome()
    {
        var outcomes = await Runner.RunTaskAsync("run-1", "team-T-001", Task("T-001",
            Auto("seeded", "true", cleanup: "exit 7")), 1, _dir, default);

        Assert.Equal([("seeded", 0), ("cleanup: seeded", 7)], outcomes.Select(o => (o.Name, o.ExitCode)));
    }

    [Fact]
    public async Task A_timeout_kills_the_process_tree()
    {
        var started = DateTime.UtcNow;
        var outcomes = await Runner.RunTaskAsync("run-1", "team-T-001", Task("T-001",
            Auto("slow", $"(sleep 30 & echo $! > {_out}/child.pid; wait)", timeoutSeconds: 1)), 1, _dir, default);

        Assert.Equal(124, Assert.Single(outcomes).ExitCode);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        var pid = int.Parse(File.ReadAllText(Path.Combine(_out, "child.pid")).Trim());
        await System.Threading.Tasks.Task.Delay(200);
        Assert.False(Directory.Exists($"/proc/{pid}"), $"child {pid} survived the timeout");
    }

    [Fact]
    public async Task Secrets_never_reach_the_child_and_the_namespace_does()
    {
        Environment.SetEnvironmentVariable("FACTORY_GATEWAY_KEY", "sk-should-not-leak");
        try
        {
            var outcomes = await Runner.RunTaskAsync("run-1", "team-T-001", Task("T-001",
                Auto("env", $"env > {_out}/env.txt")), 2, _dir, default);

            Assert.Equal(0, Assert.Single(outcomes).ExitCode);
            var env = File.ReadAllText(Path.Combine(_out, "env.txt"));
            Assert.DoesNotContain("sk-should-not-leak", env);
            Assert.DoesNotContain("FACTORY_GATEWAY_KEY", env);
            Assert.Contains("FACTORY_VERIFY_NS=run-1-T-001-2", env);
            Assert.Contains("FACTORY_TASK_ID=T-001", env);
            Assert.Contains("PATH=", env);
        }
        finally { Environment.SetEnvironmentVariable("FACTORY_GATEWAY_KEY", null); }
    }

    [Fact]
    public async Task Manual_checks_are_ledgered_pending_and_produce_no_outcome()
    {
        var outcomes = await Runner.RunTaskAsync("run-1", "team-T-001", Task("T-001",
            Auto("unit", "true"), Manual("log format reviewed")), 1, _dir, default);

        Assert.Equal(["unit"], outcomes.Select(o => o.Name));
        Assert.Contains(_ledger.Events, e => e.Kind == LedgerKinds.Verify && e.Outcome == "manual_pending" && e.TaskId == "T-001");
        var summary = Assert.Single(_ledger.Events, e => e.Kind == LedgerKinds.Verify && e.Outcome is "passed" or "failed");
        Assert.Equal(("passed", "team-T-001"), (summary.Outcome, summary.TeamId));
        Assert.Contains("\"attempt\":1", summary.Payload);
        Assert.Contains("\"sandbox\":\"local\"", summary.Payload);
    }

    [Fact]
    public void JUnit_names_tests_by_class_and_name_and_leaves_out_skipped()
    {
        var results = Factory.S2.Verify.JUnit.Parse("""
            <testsuites><testsuite name="s">
              <testcase classname="A" name="ok"/>
              <testcase classname="A" name="bad"><failure message="x"/></testcase>
              <testcase name="boom"><error message="y"/></testcase>
              <testcase classname="A" name="later"><skipped/></testcase>
            </testsuite></testsuites>
            """);
        Assert.Equal([new("A.ok", true), new("A.bad", false), new("boom", false)], results);
    }
}
