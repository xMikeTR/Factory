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
        finally { File.Delete(path); var runs = Path.ChangeExtension(path, ".runs"); if (Directory.Exists(runs)) Directory.Delete(runs, true); }
    }

    [Fact]
    public async Task A_line_cut_short_by_a_crash_is_skipped_and_the_rest_still_read()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ledger-{Guid.NewGuid():N}.jsonl");
        try
        {
            var ledger = new JsonlLedger(path);
            await ledger.AppendAsync(new LedgerEvent("r1", "S2", "run", LedgerKinds.RunStarted));
            await File.AppendAllTextAsync(ledger.RunFile("r1"), "{\"run_id\":\"r1\",\"sys\n");
            await ledger.AppendAsync(new LedgerEvent("r1", "S2", "run", LedgerKinds.RunAborted));

            var kinds = new List<string>();
            await foreach (var e in ledger.ReadAsync("r1")) kinds.Add(e.Kind);

            Assert.Equal([LedgerKinds.RunStarted, LedgerKinds.RunAborted], kinds);
        }
        finally { File.Delete(path); var runs = Path.ChangeExtension(path, ".runs"); if (Directory.Exists(runs)) Directory.Delete(runs, true); }
    }

    [Fact]
    public async Task Each_run_has_its_own_file_and_a_read_sees_only_its_run()
    {
        var dir = Directory.CreateTempSubdirectory("ledger").FullName;
        try
        {
            var path = Path.Combine(dir, "ledger.jsonl");
            var ledger = new JsonlLedger(path);
            await ledger.AppendAsync(new LedgerEvent("a", "S2", "run", LedgerKinds.RunStarted));
            await ledger.AppendAsync(new LedgerEvent("b", "S2", "run", LedgerKinds.RunStarted));
            await ledger.AppendAsync(new LedgerEvent("a", "S2", "run", LedgerKinds.RunAborted));
            Assert.False(File.Exists(path), "nothing goes in the shared file any more");
            Assert.Equal(2, File.ReadAllLines(ledger.RunFile("a")).Length);
            Assert.Equal(1, File.ReadAllLines(ledger.RunFile("b")).Length);

            var all = new List<string>();
            await foreach (var e in ledger.ReadAllAsync()) all.Add(e.RunId);
            Assert.Equal(["a", "a", "b"], all);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task A_run_id_cannot_name_a_path_outside_the_folder_and_an_old_shared_file_is_still_read()
    {
        var dir = Directory.CreateTempSubdirectory("ledger").FullName;
        try
        {
            var path = Path.Combine(dir, "ledger.jsonl");
            var ledger = new JsonlLedger(path);
            Assert.Equal(Path.Combine(dir, "ledger.runs"), Path.GetDirectoryName(ledger.RunFile("../../x")));
            await ledger.AppendAsync(new LedgerEvent("../../x", "S2", "run", LedgerKinds.RunStarted));
            Assert.Single(Directory.GetFiles(Path.Combine(dir, "ledger.runs")));

            await File.WriteAllTextAsync(path, "{\"run_id\":\"old\",\"system\":\"S2\",\"stage\":\"run\",\"kind\":\"run_started\",\"ts\":\"2026-01-01T00:00:00+00:00\"}\n");
            var rows = new List<LedgerEvent>();
            await foreach (var e in ledger.ReadAsync("old")) rows.Add(e);
            Assert.Single(rows);
            Assert.True(ledger.Exists);
        }
        finally { Directory.Delete(dir, true); }
    }
}
