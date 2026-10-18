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

    /// A suite that timed out says nothing about which tests pass, so it is not recorded as "this suite writes no results"
    /// (which would switch the regression check off for good): the row says timed_out and a resume measures again.
    [Fact]
    public async Task A_baseline_that_times_out_is_recorded_as_such_and_measured_again_on_resume()
    {
        var runner = new VerifyRunner(_ledger, new LocalSandboxFactory(), new GitWorkspace(_dir, Path.Combine(_out, "state")), TimeSpan.FromSeconds(1));

        Assert.Null(await runner.BaselineAsync("run-1", "team-T-001", "sleep 10", _dir, default));
        Assert.Equal("timed_out", _ledger.Events.Single(e => e.Stage == "baseline").Outcome);

        Assert.Null(await runner.BaselineAsync("run-1", "team-T-001", "true", _dir, default));
        Assert.Equal(["timed_out", "no_results"], _ledger.Events.Where(e => e.Stage == "baseline").Select(e => e.Outcome));
    }

    [Fact]
    public async Task Each_check_starts_from_the_tree_as_found()
    {
        var outcomes = await Runner.RunTaskAsync("run-1", "team-T-001", Task("T-001",
            Auto("generates", "mkdir -p gen/obj && echo x > gen/obj/A.cs && echo y > log.txt"),
            Auto("sees nothing", "test ! -e gen && test ! -e log.txt")), 1, _dir, default);

        Assert.Equal([("generates", 0), ("sees nothing", 0)], outcomes.Select(o => (o.Name, o.ExitCode)));
    }

    /// A refused restore means nothing in the task can build: no check runs, and the one outcome says why.
    [Fact]
    public async Task A_refused_restore_runs_no_checks()
    {
        var runner = new VerifyRunner(_ledger, new RefusingSandboxes(["src/Api/Api.csproj: <Target> is not allowed"]), new GitWorkspace(_dir, Path.Combine(_out, "state")));

        var outcomes = await runner.RunTaskAsync("run-1", "team-T-001", Task("T-001", Auto("ran", $"touch {_out}/ran")), 1, _dir, default);

        var o = Assert.Single(outcomes);
        Assert.True(Factory.Core.RestoreRefusal.isRefusal(o));
        Assert.Contains("<Target> is not allowed", o.OutputTail);
        Assert.False(File.Exists(Path.Combine(_out, "ran")));
    }

    private sealed class RefusingSandboxes(IReadOnlyList<string> reasons) : ISandboxFactory
    {
        public async Task<ICommandSandbox> ForTeamAsync(string teamId, string worktree, CancellationToken ct) =>
            new Refusing(await new LocalSandboxFactory().ForTeamAsync(teamId, worktree, ct), reasons);

        private sealed class Refusing(ICommandSandbox inner, IReadOnlyList<string> reasons) : ICommandSandbox
        {
            public string Kind => inner.Kind;
            public IReadOnlyList<string>? RestoreRefusal => reasons;
            public Task<ProcessResult> RunAsync(string command, string workdir, TimeSpan timeout, IReadOnlyDictionary<string, string> env, CancellationToken ct) =>
                inner.RunAsync(command, workdir, timeout, env, ct);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    /// Designc writes a decision check as `.venv/bin/python checks/model.py ...`: the registry's venv, built on the host.
    /// In the runner image that venv starts the image's python without designc (exit 1, read as a code failure);
    /// python3 there is the image's venv with designc. 20261016-095826 and after.
    [Fact]
    public async Task A_decision_check_in_the_container_runs_the_registry_venv_python_as_the_image_python()
    {
        var sandboxes = new RecordingSandboxes("docker");
        var runner = new VerifyRunner(_ledger, sandboxes, new GitWorkspace(_dir, Path.Combine(_out, "state")));

        await runner.RunCommandAsync("run-1", "D-1 [script]", ".venv/bin/python checks/model.py no-retry user agent", _dir, TimeSpan.FromSeconds(5), default, "D-1");
        await runner.RunCommandAsync("run-1", "D-2 [script]", "./.venv/bin/python3 checks/model.py holds", _dir, TimeSpan.FromSeconds(5), default, "D-2");
        await runner.RunCommandAsync("run-1", "integration tests", ".venv/bin/python -m pytest", _dir, TimeSpan.FromSeconds(5), default);

        Assert.Equal(["python3 checks/model.py no-retry user agent", "python3 checks/model.py holds", ".venv/bin/python -m pytest"], sandboxes.Commands);
        Assert.Contains(_ledger.Events, e => e.Decision == "D-1" && e.Payload!.Contains("\"command\":\"python3 checks/model.py no-retry user agent\"")
            && e.Payload.Contains("\"declared\":\".venv/bin/python checks/model.py no-retry user agent\""));
    }

    [Fact]
    public async Task A_decision_check_on_the_host_runs_as_declared()
    {
        var sandboxes = new RecordingSandboxes("local");
        var runner = new VerifyRunner(_ledger, sandboxes, new GitWorkspace(_dir, Path.Combine(_out, "state")));

        await runner.RunCommandAsync("run-1", "D-1 [script]", ".venv/bin/python checks/model.py holds", _dir, TimeSpan.FromSeconds(5), default, "D-1");

        Assert.Equal([".venv/bin/python checks/model.py holds"], sandboxes.Commands);
    }

    private sealed class RecordingSandboxes(string kind) : ISandboxFactory
    {
        public List<string> Commands { get; } = [];

        public System.Threading.Tasks.Task<ICommandSandbox> ForTeamAsync(string teamId, string worktree, CancellationToken ct) => System.Threading.Tasks.Task.FromResult<ICommandSandbox>(new Recording(this, kind));

        private sealed class Recording(RecordingSandboxes owner, string kind) : ICommandSandbox
        {
            public string Kind => kind;
            public System.Threading.Tasks.Task<ProcessResult> RunAsync(string command, string workdir, TimeSpan timeout, IReadOnlyDictionary<string, string> env, CancellationToken ct)
            {
                owner.Commands.Add(command);
                return System.Threading.Tasks.Task.FromResult(new ProcessResult(0, "", TimeSpan.Zero, false));
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
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
