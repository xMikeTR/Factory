using System.Diagnostics;
using Factory.Harness;
using Xunit;

namespace Factory.Integration.Tests.Ledger;

/// A row reads back as it was written: the reader's trace is not stamped on a row written outside one, or the
/// row's projected event_id would change with every run and the registry would get it again.
public class JsonlLedgerTests
{
    [Fact]
    public async Task A_row_written_with_no_trace_reads_back_with_none_inside_a_trace()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ledger-{Guid.NewGuid():N}.jsonl");
        try
        {
            var ledger = new JsonlLedger(path);
            Assert.Null(Activity.Current);
            await ledger.AppendAsync(new LedgerEvent("r1", "S2", "run", LedgerKinds.RunAborted));

            using var source = new ActivitySource("test");
            using var listener = new ActivityListener { ShouldListenTo = _ => true, Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData };
            ActivitySource.AddActivityListener(listener);
            using var activity = source.StartActivity("reader");
            Assert.NotNull(Activity.Current);

            var rows = new List<LedgerEvent>();
            await foreach (var e in ledger.ReadAsync("r1")) rows.Add(e);

            Assert.Null(Assert.Single(rows).TraceId);
        }
        finally { File.Delete(path); }
    }
}
