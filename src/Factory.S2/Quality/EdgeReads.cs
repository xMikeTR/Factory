using Factory.Core;
using Factory.Harness.Sandbox;
using Factory.S2.Git;

namespace Factory.S2.Quality;

/// The expensive edge reads of one verify attempt (or one `check_quality` call), each done at most once however many checks want it:
/// the current and baseline assembly builds and the current and baseline dependency-cruiser reads. The hand-written `forbid` rules and
/// the design's allow list share them. Create one per attempt and dispose it: the baseline checkout lives until then.
public sealed class EdgeReads(ISandboxFactory sandboxes, GitWorkspace git, string worktree, string runId, CancellationToken outer) : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
    private CancellationToken ct => _cts.Token;
    private readonly object _gate = new();
    private Task<BaselineCheckout>? _checkout;
    private Task<IReadOnlyList<(string Dll, string Project)>>? _builtCurrent, _builtBaseline;
    private Task<IReadOnlyList<CodeEdge>>? _tsCurrent, _tsBaseline;

    private Task<BaselineCheckout> Checkout() { lock (_gate) return _checkout ??= git.BaselineCheckoutAsync(runId, ct); }

    /// The assemblies (with their projects) of the integrated worktree; empty when it does not build.
    public Task<IReadOnlyList<(string Dll, string Project)>> BuiltCurrent() { lock (_gate) return _builtCurrent ??= NamespaceGate.BuildProjectsAsync(sandboxes, "arch-current", worktree, ct); }

    /// The same on a checkout of the integration branch.
    public Task<IReadOnlyList<(string Dll, string Project)>> BuiltBaseline() { lock (_gate) return _builtBaseline ??= BuildBaselineAsync(); }

    private async Task<IReadOnlyList<(string Dll, string Project)>> BuildBaselineAsync() =>
        await NamespaceGate.BuildProjectsAsync(sandboxes, "arch-baseline", (await Checkout()).Path, ct);

    public Task<IReadOnlyList<CodeEdge>> TsCurrent() { lock (_gate) return _tsCurrent ??= TsGate.EdgesAsync(sandboxes, "ts-current", worktree, ct); }

    public Task<IReadOnlyList<CodeEdge>> TsBaseline() { lock (_gate) return _tsBaseline ??= TsBaselineAsync(); }

    private async Task<IReadOnlyList<CodeEdge>> TsBaselineAsync() => await TsGate.EdgesAsync(sandboxes, "ts-baseline", (await Checkout()).Path, ct);

    public async ValueTask DisposeAsync()
    {
        Task<BaselineCheckout>? checkout;
        lock (_gate) checkout = _checkout;
        _cts.Cancel(); // a read still running (a check that ran out of budget) stops here instead of holding the caller
        // let reads still in flight finish before the checkout they read is removed
        foreach (var t in new Task?[] { _builtCurrent, _tsCurrent, _builtBaseline, _tsBaseline }) { if (t is not null) try { await t; } catch { } }
        if (checkout is not null) try { await (await checkout).DisposeAsync(); } catch { }
        _cts.Dispose();
    }
}
