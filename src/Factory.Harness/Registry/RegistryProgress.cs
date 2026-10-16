namespace Factory.Harness.Registry;

/// Projects a run onto the registry: its own run stream, then the fold into designc's main streams.
/// Idempotent (rows dedupe on event_id), so calling it after every event and again at Finalize is safe.
public static class RegistryProgress
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task SyncAsync(ILedger ledger, string registryPath, string runId, HandoverRef handover, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var run = new List<LedgerEvent>();
            await foreach (var e in ledger.ReadAsync(runId, ct)) run.Add(e);
            await RunLedgerStream.AppendAsync(registryPath, runId, RegistryLedgerProjector.Project(run, handover), ct);
            await LedgerFold.RunAsync(registryPath, ct);
        }
        finally { Gate.Release(); }
    }
}

/// An ILedger that, after an attempt starts, a task changes state or the attempt ends, projects the run
/// onto the registry so designc's Build view moves with it. The event is always logged first; projecting
/// is best effort and never fails the run (no registry folder, or no handover copy yet, projects nothing).
public sealed class RegistryProgressLedger(ILedger inner, string registryPath, Func<string, HandoverRef> handover) : ILedger
{
    private static readonly HashSet<string> Progress = [LedgerKinds.RunStarted, LedgerKinds.TaskEvent, LedgerKinds.Run, LedgerKinds.RunAborted];

    public async ValueTask AppendAsync(LedgerEvent e, CancellationToken ct = default)
    {
        await inner.AppendAsync(e, ct);
        if (!Progress.Contains(e.Kind) || !Directory.Exists(registryPath)) return;
        try { await RegistryProgress.SyncAsync(inner, registryPath, e.RunId, handover(e.RunId), ct); }
        catch (Exception x) when (x is not OperationCanceledException)
        {
            Console.Error.WriteLine($"registry progress not written ({x.GetType().Name}: {x.Message}); Finalize writes it again");
        }
    }

    public IAsyncEnumerable<LedgerEvent> ReadAsync(string runId, CancellationToken ct = default) => inner.ReadAsync(runId, ct);
}

/// How an attempt that never reached Finalize ended (crash, Ctrl+C, workflow error).
public static class RunAttempt
{
    /// Logs RunAborted when the latest attempt started and has no end yet; true when it did.
    public static async Task<bool> EndAsync(ILedger ledger, string runId, string status, string detail, CancellationToken ct)
    {
        bool started = false, ended = false;
        await foreach (var e in ledger.ReadAsync(runId, ct))
        {
            if (e.Kind == LedgerKinds.RunStarted) (started, ended) = (true, false);
            else if (e.Kind == LedgerKinds.Run && e.TaskId is null || e.Kind == LedgerKinds.RunAborted) ended = true;
        }
        if (!started || ended) return false;
        await ledger.AppendAsync(new LedgerEvent(runId, "S2", "host", LedgerKinds.RunAborted, Outcome: status,
            Payload: System.Text.Json.JsonSerializer.Serialize(new { status, detail })), ct);
        return true;
    }
}
