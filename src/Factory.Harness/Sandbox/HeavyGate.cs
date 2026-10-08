namespace Factory.Harness.Sandbox;

/// Serialises heavy commands (builds, test runs, verify) across teams: CPU contention is what turns
/// a passing check into a timeout. `slots` run at once; `EnterExclusiveAsync` waits for all of them
/// to drain and keeps new ones out, for the isolated re-run of a command that timed out.
public sealed class HeavyGate(int slots)
{
    public static int DefaultSlots => Math.Max(1, Environment.ProcessorCount / 4);

    private readonly SemaphoreSlim _slots = new(slots, slots);
    private readonly SemaphoreSlim _turnstile = new(1, 1); // an exclusive entrant holds it, so newcomers queue behind it

    public async Task<IAsyncDisposable> EnterAsync(CancellationToken ct)
    {
        await _turnstile.WaitAsync(ct);
        _turnstile.Release();
        await _slots.WaitAsync(ct);
        return new Release(() => _slots.Release());
    }

    public async Task<IAsyncDisposable> EnterExclusiveAsync(CancellationToken ct)
    {
        await _turnstile.WaitAsync(ct);
        var taken = 0;
        try
        {
            for (; taken < slots; taken++) await _slots.WaitAsync(ct);
        }
        catch
        {
            _slots.Release(taken);
            _turnstile.Release();
            throw;
        }
        return new Release(() => { _slots.Release(slots); _turnstile.Release(); });
    }

    private sealed class Release(Action release) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { release(); return ValueTask.CompletedTask; }
    }
}

/// Every command of every sandbox it hands out goes through the gate. A command that times out is
/// never taken as evidence: it is re-run once with the machine to itself, and only a second
/// timeout is reported (as one, exit 124). A command the repo's policy marks flaky (`quality.flaky`) gets the same single
/// isolated re-run after a plain failure; the second answer stands, and `onFlakyRetry` is told so the retry is never hidden.
public sealed class GatedSandboxFactory(ISandboxFactory inner, HeavyGate gate, Func<string, bool>? isFlaky = null, Action<string>? onFlakyRetry = null) : ISandboxFactory
{
    public async Task<ICommandSandbox> ForTeamAsync(string teamId, string worktree, CancellationToken ct) =>
        new GatedSandbox(await inner.ForTeamAsync(teamId, worktree, ct), gate, isFlaky, onFlakyRetry);

    private sealed class GatedSandbox(ICommandSandbox sandbox, HeavyGate gate, Func<string, bool>? isFlaky, Action<string>? onFlakyRetry) : ICommandSandbox
    {
        public string Kind => sandbox.Kind;
        public IReadOnlyList<string>? RestoreRefusal => sandbox.RestoreRefusal;

        public async Task<ProcessResult> RunAsync(string command, string workdir, TimeSpan timeout,
            IReadOnlyDictionary<string, string> env, CancellationToken ct)
        {
            ProcessResult result;
            await using (await gate.EnterAsync(ct))
                result = await sandbox.RunAsync(command, workdir, timeout, env, ct);
            var retry = result.TimedOut || (result.ExitCode != 0 && isFlaky?.Invoke(command) == true);
            if (!retry) return result;
            if (!result.TimedOut) onFlakyRetry?.Invoke(command);

            await using (await gate.EnterExclusiveAsync(ct))
                return await sandbox.RunAsync(command, workdir, timeout, env, ct);
        }

        public ValueTask DisposeAsync() => sandbox.DisposeAsync();
    }
}
