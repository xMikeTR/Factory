using Factory.Harness;
using Factory.Harness.Sandbox;
using Xunit;

namespace Factory.Integration.Tests.Sandbox;

/// CPU contention makes checks time out; heavy commands are serialised and a timeout is never evidence.
public class HeavyGateTests
{
    /// Runs `script` per call: how long it holds the slot and whether it reports a timeout.
    private sealed class FakeSandbox(Func<int, (TimeSpan Hold, bool TimedOut)> script) : ICommandSandbox
    {
        private int _calls, _running, _peak;
        public int Calls => _calls;
        public int Peak => _peak;
        public System.Collections.Concurrent.ConcurrentDictionary<int, int> RunningWith { get; } = new();
        public string Kind => "fake";

        public async Task<ProcessResult> RunAsync(string command, string workdir, TimeSpan timeout, IReadOnlyDictionary<string, string> env, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref _calls);
            var now = Interlocked.Increment(ref _running);
            RunningWith[call] = now;
            int peak;
            while ((peak = _peak) < now && Interlocked.CompareExchange(ref _peak, now, peak) != peak) { }
            var (hold, timedOut) = script(call);
            await Task.Delay(hold, ct);
            Interlocked.Decrement(ref _running);
            return new ProcessResult(timedOut ? 124 : 0, "", hold, timedOut);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeFactory(ICommandSandbox sandbox) : ISandboxFactory
    {
        public Task<ICommandSandbox> ForTeamAsync(string teamId, string worktree, CancellationToken ct) => Task.FromResult(sandbox);
    }

    private static Task<ProcessResult> Run(ICommandSandbox s) => s.RunAsync("c", ".", TimeSpan.FromSeconds(5), new Dictionary<string, string>(), default);

    [Fact]
    public async Task No_more_heavy_commands_run_at_once_than_there_are_slots()
    {
        var inner = new FakeSandbox(_ => (TimeSpan.FromMilliseconds(40), false));
        var gated = await new GatedSandboxFactory(new FakeFactory(inner), new HeavyGate(2)).ForTeamAsync("t", ".", default);

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Run(gated)));

        Assert.Equal(6, inner.Calls);
        Assert.Equal(2, inner.Peak);
    }

    [Fact]
    public async Task A_timeout_is_re_run_alone_and_the_second_answer_stands()
    {
        var inner = new FakeSandbox(call => (TimeSpan.FromMilliseconds(30), TimedOut: call == 1));
        var gated = await new GatedSandboxFactory(new FakeFactory(inner), new HeavyGate(3)).ForTeamAsync("t", ".", default);

        var result = await Run(gated);

        Assert.False(result.TimedOut);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task A_second_timeout_is_reported_as_one_and_not_run_a_third_time()
    {
        var inner = new FakeSandbox(_ => (TimeSpan.FromMilliseconds(30), true));
        var gated = await new GatedSandboxFactory(new FakeFactory(inner), new HeavyGate(1)).ForTeamAsync("t", ".", default);

        var result = await Run(gated);

        Assert.True(result.TimedOut);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task The_isolated_re_run_waits_for_every_other_command_to_finish()
    {
        // call 1 times out at once; the others hold their slots for a while: the re-run must see none of them running.
        var inner = new FakeSandbox(call => call == 1 ? (TimeSpan.FromMilliseconds(5), true) : (TimeSpan.FromMilliseconds(80), false));
        var gated = await new GatedSandboxFactory(new FakeFactory(inner), new HeavyGate(3)).ForTeamAsync("t", ".", default);

        // Each Run reaches the fake (and numbers its call) synchronously, so calls 1-3 are all in flight before any await yields.
        var timedOut = Run(gated);
        var others = Task.WhenAll(Run(gated), Run(gated));
        await Task.WhenAll(timedOut, others);

        Assert.Equal(4, inner.Calls);
        Assert.Equal(1, inner.RunningWith[4]); // the isolated re-run started with nothing else running
    }
}
