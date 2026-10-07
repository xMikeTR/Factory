using System.Runtime.CompilerServices;
using Factory.Harness;

namespace Factory.Integration.Tests.Support;

public sealed class MemoryLedger : ILedger
{
    private readonly List<LedgerEvent> _events = [];

    public IReadOnlyList<LedgerEvent> Events { get { lock (_events) return _events.ToList(); } }

    public ValueTask AppendAsync(LedgerEvent e, CancellationToken ct = default)
    {
        lock (_events) _events.Add(e);
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<LedgerEvent> ReadAsync(string runId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var e in Events.Where(e => e.RunId == runId)) yield return e;
        await Task.CompletedTask;
    }
}
