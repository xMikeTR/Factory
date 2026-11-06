using System.Diagnostics;
using Factory.Harness;
using Factory.S2;
using Factory.S2.Git;
using Xunit;

namespace Factory.Integration.Tests.Workflow;

/// `close` on a split run: complete only when every repo's PR is merged; cleanup waits for all.
public sealed class SplitCloseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "s2-close-" + Guid.NewGuid().ToString("N"));
    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    private static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        return "";
    }

    private string NewRepo(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        Git(path, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(path, "README.md"), name);
        Git(path, "add", ".");
        Git(path, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "init");
        return path;
    }

    private async Task<ILedger> ReadyForReviewAsync()
    {
        Directory.CreateDirectory(_root);
        var ledger = new JsonlLedger(Path.Combine(_root, "ledger.jsonl"));
        await ledger.AppendAsync(new LedgerEvent("r1", "S2", "finalize", LedgerKinds.Run, Outcome: "ready_for_review"), default);
        return ledger;
    }

    private static Func<CancellationToken, Task<RunClose.MergedCheck>> Green => _ => Task.FromResult(new RunClose.MergedCheck("abc", null));

    private static bool Closed(IEnumerable<LedgerEvent> rows) => rows.Any(e => e is { Kind: LedgerKinds.Merge, Stage: "close", Outcome: "merged" });

    private async Task<List<LedgerEvent>> RowsAsync(ILedger ledger)
    {
        var rows = new List<LedgerEvent>();
        await foreach (var e in ledger.ReadAsync("r1", default)) rows.Add(e);
        return rows;
    }

    private static RunClose.RepoMerge Repo(string name, string? merged) => new(name, _ => Task.FromResult(merged));

    [Fact]
    public async Task One_repo_merged_is_not_complete_and_names_the_repo_still_open()
    {
        var ledger = await ReadyForReviewAsync();
        var split = new RunClose.SplitMerged([Repo("Api", "pr https://host/api/pull/1"), Repo("Ui", null)]);

        var why = await RunClose.RecordAsync(ledger, "r1", split.AllAsync, Green, null, canState: false, default, split.NotMerged);

        Assert.Equal("not every pull request is merged yet: Ui", why);
        Assert.False(Closed(await RowsAsync(ledger)));
    }

    [Fact]
    public async Task Both_merged_is_complete_and_records_both_urls()
    {
        var ledger = await ReadyForReviewAsync();
        var split = new RunClose.SplitMerged([Repo("Api", "pr https://host/api/pull/1"), Repo("Ui", "pr https://host/ui/pull/2")]);

        Assert.Null(await RunClose.RecordAsync(ledger, "r1", split.AllAsync, Green, null, canState: false, default, split.NotMerged));

        var row = Assert.Single(await RowsAsync(ledger), e => e is { Stage: "close", Outcome: "merged" });
        Assert.Contains("Api: pr https://host/api/pull/1", row.Payload);
        Assert.Contains("Ui: pr https://host/ui/pull/2", row.Payload);
    }

    [Fact]
    public async Task Cleanup_removes_the_worktrees_of_every_repo_only_once_closed()
    {
        var api = new GitWorkspace(NewRepo("api"), Path.Combine(_root, "state"), "Api");
        var ui = new GitWorkspace(NewRepo("ui"), Path.Combine(_root, "state"), "Ui");
        await api.EnsureIntegrationAsync("r1", "main", default);
        await ui.EnsureIntegrationAsync("r1", "main", default);
        var ledger = await ReadyForReviewAsync();

        Assert.False(await RunClose.CleanUpAsync(ledger, [api, ui], "r1", default));
        Assert.True(Directory.Exists(api.IntegrationPath("r1")) && Directory.Exists(ui.IntegrationPath("r1")));

        await ledger.AppendAsync(new LedgerEvent("r1", "S2", "close", LedgerKinds.Merge, Outcome: "merged"), default);

        Assert.True(await RunClose.CleanUpAsync(ledger, [api, ui], "r1", default));
        Assert.False(Directory.Exists(api.IntegrationPath("r1")) || Directory.Exists(ui.IntegrationPath("r1")));
    }
}
