using System.Diagnostics;
using Factory.Harness;
using Factory.S2;
using Factory.S2.Git;
using Xunit;

namespace Factory.Integration.Tests.Workflow;

/// A split run over the Pricing.Api / Pricing.Portal fixtures (pricing-sample split in two), real git repos, fake PR hosts:
/// one PR per repo, Api first, the factory never merges; closed only when both PRs are merged.
public sealed class SplitRunTests : IDisposable
{
    private const string Run = "r1";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "s2-split-" + Guid.NewGuid().ToString("N"));
    private string State => Path.Combine(_root, "state");

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    private static void Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-c", "user.name=t", "-c", "user.email=t@t" }.Concat(args)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    private static void Copy(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(dir.Replace(from, to));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories)) File.Copy(file, file.Replace(from, to), true);
    }

    /// The fixture as a git repo with one commit on main.
    private string Fixture(string name)
    {
        var path = Path.Combine(_root, name);
        Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "split", name), path);
        Git(path, "init", "-q", "-b", "main");
        Git(path, "add", ".");
        Git(path, "commit", "-q", "-m", "init");
        return path;
    }

    private sealed class Host(string name) : IPullRequests, IMergedPullRequests
    {
        public bool Merged { get; set; }
        public List<string> Opened { get; } = [];
        public Task<string> EnsurePullRequestAsync(string head, string baseBranch, string title, string body, CancellationToken ct)
        {
            Opened.Add(body);
            return Task.FromResult($"https://host/{name}/pull/1");
        }
        public Task EnableAutoMergeAsync(string url, CancellationToken ct) => throw new InvalidOperationException("the factory never merges a split run");
        public Task<string?> MergedPullRequestAsync(string head, CancellationToken ct) =>
            Task.FromResult(Merged ? $"https://host/{name}/pull/1" : null);
    }

    private async Task<(GitWorkspace Api, GitWorkspace Ui, Host ApiHost, Host UiHost, ILedger Ledger, RunClose.SplitMerged Merged)> SetUpAsync()
    {
        var api = new GitWorkspace(Fixture("Pricing.Api"), State, "Api");
        var ui = new GitWorkspace(Fixture("Pricing.Portal"), State, "Ui");
        foreach (var (g, file) in new[] { (api, "src/Pricing.Data/Quote.cs"), (ui, "src/Pricing.Portal/Program.cs") })
        {
            var wt = await g.EnsureIntegrationAsync(Run, "main", default);
            File.AppendAllText(Path.Combine(wt, file), "// change\n");
            Git(wt, "commit", "-q", "-am", "change");
        }
        Directory.CreateDirectory(_root);
        var ledger = new JsonlLedger(Path.Combine(_root, "ledger.jsonl"));
        var apiHost = new Host("api");
        var uiHost = new Host("ui");
        var merged = new RunClose.SplitMerged([
            new("Api", ct => MergedAsync(apiHost, ct)),
            new("Ui", ct => MergedAsync(uiHost, ct))]);
        return (api, ui, apiHost, uiHost, ledger, merged);

        static async Task<string?> MergedAsync(Host h, CancellationToken ct) => await h.MergedPullRequestAsync("factory/r1/integration", ct) is { } url ? $"pr {url}" : null;
    }

    private static Task<string?> CloseAsync(ILedger ledger, RunClose.SplitMerged merged) =>
        RunClose.RecordAsync(ledger, Run, merged.AllAsync, _ => Task.FromResult(new RunClose.MergedCheck("abc", null)), null, canState: false, default, merged.NotMerged);

    private static async Task<bool> ClosedAsync(ILedger ledger)
    {
        await foreach (var e in ledger.ReadAsync(Run, default))
            if (e is { Kind: LedgerKinds.Merge, Stage: "close", Outcome: "merged" }) return true;
        return false;
    }

    [Fact]
    public async Task A_two_repo_run_opens_one_PR_per_repo_Api_first_and_ends_ready_for_review()
    {
        var (api, ui, apiHost, uiHost, ledger, _) = await SetUpAsync();

        var prs = await SplitMerge.OpenAsync(["Ui", "Api"], Run, "main", n => n == "Api" ? apiHost : uiHost,
            n => $"[factory] {n}", n => SplitMerge.MergeOrderNote(["Api", "Ui"], Run), default);
        await ledger.AppendAsync(new LedgerEvent(Run, "S2", "finalize", LedgerKinds.Run, Outcome: SplitMerge.Finished(Run, prs).Status), default);

        Assert.Equal("Api: https://host/api/pull/1\nUi: https://host/ui/pull/1", SplitMerge.Finished(Run, prs).Detail);
        Assert.Contains("Merge the Api PR first", Assert.Single(apiHost.Opened));
        Assert.Contains("Merge the Api PR first", Assert.Single(uiHost.Opened));
        // each PR comes from its own repo's integration branch, which carries that repo's change only
        Assert.Equal(["src/Pricing.Portal/Program.cs"], (await ui.DiffStatsAsync(Run, "main", default)).Files);
        Assert.Equal(["src/Pricing.Data/Quote.cs"], (await api.DiffStatsAsync(Run, "main", default)).Files);
    }

    [Fact]
    public async Task One_merged_is_not_done_and_nothing_is_cleaned_up()
    {
        var (api, ui, apiHost, _, ledger, merged) = await SetUpAsync();
        await ledger.AppendAsync(new LedgerEvent(Run, "S2", "finalize", LedgerKinds.Run, Outcome: "ready_for_review"), default);
        apiHost.Merged = true;

        Assert.Equal("not every pull request is merged yet: Ui", await CloseAsync(ledger, merged));
        Assert.False(await RunClose.CleanUpAsync(ledger, [api, ui], Run, default));
        Assert.True(Directory.Exists(api.IntegrationPath(Run)) && Directory.Exists(ui.IntegrationPath(Run)));
    }

    [Fact]
    public async Task Both_merged_is_done_and_both_repos_are_cleaned_up()
    {
        var (api, ui, apiHost, uiHost, ledger, merged) = await SetUpAsync();
        await ledger.AppendAsync(new LedgerEvent(Run, "S2", "finalize", LedgerKinds.Run, Outcome: "ready_for_review"), default);
        apiHost.Merged = uiHost.Merged = true;

        Assert.Null(await CloseAsync(ledger, merged));
        Assert.True(await ClosedAsync(ledger));
        Assert.True(await RunClose.CleanUpAsync(ledger, [api, ui], Run, default));
        Assert.False(Directory.Exists(api.IntegrationPath(Run)) || Directory.Exists(ui.IntegrationPath(Run)));
    }

    /// The single-repo path is as before: no split, one PR host, and the review-mode run (A_review_run_ends_ready_for_review...)
    /// still ends at one PR that `close` checks with the unchanged single-repo path.
    [Fact]
    public async Task A_single_repo_run_is_not_split_and_uses_its_one_PR_host()
    {
        using var h = await Support.TeamHarness.CreateAsync(new Support.ScriptedChatClient(), new Factory.Core.Limits(8, 3));
        Assert.False(h.Services.IsSplit);
        Assert.Same(h.Services.PullRequests, h.Services.PullRequestsFor("Api"));
        Assert.False(File.Exists(h.Services.Settings.ReposPath(Support.TeamHarness.RunId)));
    }
}
