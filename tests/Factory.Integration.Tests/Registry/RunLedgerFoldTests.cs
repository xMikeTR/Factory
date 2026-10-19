using Factory.Harness.Registry;
using Factory.Integration.Tests.Support;
using Xunit;

namespace Factory.Integration.Tests.Registry;

public class RunLedgerFoldTests
{
    private static string NewRegistry() => Path.Combine(Path.GetTempPath(), "s2-fold-" + Guid.NewGuid().ToString("N"));

    private static RegistryEvent Row(string id, string kind, string stream) =>
        new(stream, id, $$"""{"by":"factory-s2","event_id":"{{id}}","kind":"{{kind}}","schema_version":1,"ts":1}""");

    [Fact]
    public void Every_kind_S2_writes_lands_in_the_stream_designc_gives_it()
    {
        foreach (var kind in RegistryStreams.Kinds)
            Assert.Equal(DesigncSchema.StreamOf(kind), RegistryStreams.Of(kind));
    }

    [Fact]
    public async Task A_run_appends_only_to_its_own_stream_until_the_fold_runs()
    {
        var registry = NewRegistry();
        await RunLedgerStream.AppendAsync(registry, "run-a", [Row("s2-a", "task", "pipeline.jsonl")], CancellationToken.None);

        Assert.True(File.Exists(RunLedgerStream.PathOf(registry, "run-a")));
        Assert.False(File.Exists(Path.Combine(registry, "ledger", "pipeline.jsonl")));
    }

    [Fact]
    public async Task Folding_merges_every_run_into_its_kinds_stream_and_folding_again_appends_nothing()
    {
        var registry = NewRegistry();
        await RunLedgerStream.AppendAsync(registry, "run-a", [Row("s2-a", "task", "pipeline.jsonl"), Row("s2-c", "check", "checks.jsonl")], CancellationToken.None);
        await RunLedgerStream.AppendAsync(registry, "run-b", [Row("s2-b", "task", "pipeline.jsonl"), Row("s2-a", "task", "pipeline.jsonl")], CancellationToken.None);

        var first = await LedgerFold.RunAsync(registry, CancellationToken.None);
        var second = await LedgerFold.RunAsync(registry, CancellationToken.None);

        var pipeline = await File.ReadAllLinesAsync(Path.Combine(registry, "ledger", "pipeline.jsonl"));
        var checks = await File.ReadAllLinesAsync(Path.Combine(registry, "ledger", "checks.jsonl"));
        Assert.Equal(2, pipeline.Length);
        Assert.Single(checks);
        Assert.Equal(new FoldReport(2, 3, 0), first);
        Assert.Equal(new FoldReport(2, 0, 0), second);
    }

    [Fact]
    public async Task Concurrent_folds_serialise_and_each_row_lands_once()
    {
        var registry = NewRegistry();
        for (var i = 0; i < 5; i++)
            await RunLedgerStream.AppendAsync(registry, $"run-{i}", [Row($"s2-{i}", "task", "pipeline.jsonl")], CancellationToken.None);

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => LedgerFold.RunAsync(registry, CancellationToken.None))));

        Assert.Equal(5, (await File.ReadAllLinesAsync(Path.Combine(registry, "ledger", "pipeline.jsonl"))).Distinct().Count());
    }

    [Fact]
    public async Task A_row_of_an_unknown_kind_is_counted_not_folded()
    {
        var registry = NewRegistry();
        var runs = Path.Combine(registry, "ledger", "runs");
        Directory.CreateDirectory(runs);
        await File.WriteAllTextAsync(Path.Combine(runs, "run-x.jsonl"), """{"event_id":"s2-x","kind":"mystery"}""" + "\n");

        var report = await LedgerFold.RunAsync(registry, CancellationToken.None);

        Assert.Equal(new FoldReport(1, 0, 1), report);
        Assert.False(File.Exists(Path.Combine(registry, "ledger", "pipeline.jsonl")));
    }

    [Fact]
    public async Task A_row_whose_id_or_kind_is_not_a_string_is_counted_not_a_crash()
    {
        var registry = NewRegistry();
        var runs = Path.Combine(registry, "ledger", "runs");
        Directory.CreateDirectory(runs);
        await File.WriteAllTextAsync(Path.Combine(runs, "run-y.jsonl"),
            """{"event_id":5,"kind":"task"}""" + "\n" + """{"event_id":"s2-y","kind":7}""" + "\n");

        var report = await LedgerFold.RunAsync(registry, CancellationToken.None);

        Assert.Equal(new FoldReport(1, 0, 2), report);
    }

    [Fact]
    public async Task A_fold_lock_that_is_never_released_is_an_error_naming_the_file_not_a_hang()
    {
        var registry = NewRegistry();
        await RunLedgerStream.AppendAsync(registry, "run-a", [Row("s2-a", "task", "pipeline.jsonl")], CancellationToken.None);
        var lockFile = Path.Combine(registry, "ledger", ".fold.lock");
        await using var held = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var e = await Assert.ThrowsAsync<IOException>(() => LedgerFold.RunAsync(registry, CancellationToken.None, TimeSpan.FromMilliseconds(150)));

        Assert.Contains(".fold.lock", e.Message);
    }
}
