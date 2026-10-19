using Factory.Harness;
using Factory.Harness.Sandbox;
using Xunit;

namespace Factory.Integration.Tests.Sandbox;

/// FACTORY_SANDBOX=docker,local: the first sandbox that starts is used, the ones before it that could not are named.
public class FallbackSandboxFactoryTests
{
    private sealed class Stub(string kind) : ICommandSandbox
    {
        public string Kind => kind;
        public Task<ProcessResult> RunAsync(string command, string workdir, TimeSpan timeout, IReadOnlyDictionary<string, string> env, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Fails(string why) : ISandboxFactory
    {
        public Task<ICommandSandbox> ForTeamAsync(string teamId, string worktree, CancellationToken ct) => throw new InvalidOperationException(why);
    }

    private sealed class Works(string kind) : ISandboxFactory
    {
        public Task<ICommandSandbox> ForTeamAsync(string teamId, string worktree, CancellationToken ct) => Task.FromResult<ICommandSandbox>(new Stub(kind));
    }

    [Fact]
    public async Task The_first_sandbox_that_starts_is_used_and_each_failure_before_it_is_reported()
    {
        var skipped = new List<string>();
        var f = new FallbackSandboxFactory([("first", new Fails("no url")), ("docker", new Fails("docker not found")), ("local", new Works("local"))],
            (name, why) => skipped.Add($"{name}: {why}"));

        await using var s = await f.ForTeamAsync("t", "/w", default);

        Assert.Equal("local", s.Kind);
        Assert.Equal(["first: no url", "docker: docker not found"], skipped);
    }

    [Fact]
    public async Task When_the_first_works_nothing_else_is_tried()
    {
        var skipped = new List<string>();
        var f = new FallbackSandboxFactory([("docker", new Works("docker")), ("local", new Fails("must not run"))], (n, w) => skipped.Add(n));
        await using var s = await f.ForTeamAsync("t", "/w", default);
        Assert.Equal("docker", s.Kind);
        Assert.Empty(skipped);
    }

    [Fact]
    public async Task When_none_starts_the_error_lists_every_reason()
    {
        var f = new FallbackSandboxFactory([("first", new Fails("a")), ("docker", new Fails("b"))], (n, w) => { });
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => f.ForTeamAsync("t", "/w", default));
        Assert.Contains("first: a", e.Message);
        Assert.Contains("docker: b", e.Message);
    }

    [Fact]
    public async Task A_cancelled_start_is_not_a_fallback()
    {
        var cts = new CancellationTokenSource(); cts.Cancel();
        var f = new FallbackSandboxFactory([("docker", new CancelsFactory()), ("local", new Works("local"))], (n, w) => { });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.ForTeamAsync("t", "/w", cts.Token));
    }

    private sealed class CancelsFactory : ISandboxFactory
    {
        public Task<ICommandSandbox> ForTeamAsync(string teamId, string worktree, CancellationToken ct) { ct.ThrowIfCancellationRequested(); throw new InvalidOperationException(); }
    }

    [Fact]
    public void The_list_parses_in_order_and_refuses_unknown_names()
    {
        Assert.Equal(["docker", "local"], FallbackSandboxFactory.Parse("docker ,local"));
        Assert.Equal(["docker"], FallbackSandboxFactory.Parse(null));
        Assert.Throws<ArgumentException>(() => FallbackSandboxFactory.Parse("docker,podman"));
        Assert.Throws<ArgumentException>(() => FallbackSandboxFactory.Parse("hub,docker"));
        Assert.Throws<ArgumentException>(() => FallbackSandboxFactory.Parse("docker,docker"));
    }
}
