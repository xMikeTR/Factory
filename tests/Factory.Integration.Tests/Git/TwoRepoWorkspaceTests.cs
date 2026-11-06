using System.Diagnostics;
using Factory.Core;
using Factory.Integration.Tests.Support;
using Factory.S2.Git;
using Xunit;

namespace Factory.Integration.Tests.Git;

public sealed class TwoRepoWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "s2-two-" + Guid.NewGuid().ToString("N"));
    private string State => Path.Combine(_root, "state");

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
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

    private static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        return o;
    }

    [Fact]
    public async Task Two_repos_get_their_own_integration_worktrees_for_one_run()
    {
        var api = new GitWorkspace(NewRepo("api"), State, "Api");
        var ui = new GitWorkspace(NewRepo("ui"), State, "Ui");

        var a = await api.EnsureIntegrationAsync("r1", "main", CancellationToken.None);
        var u = await ui.EnsureIntegrationAsync("r1", "main", CancellationToken.None);

        Assert.NotEqual(a, u);
        Assert.Equal("api", await File.ReadAllTextAsync(Path.Combine(a, "README.md")));
        Assert.Equal("ui", await File.ReadAllTextAsync(Path.Combine(u, "README.md")));
        Assert.Contains("factory/r1/integration", Git(api.RepoPath, "branch", "--list"));
        Assert.Contains("factory/r1/integration", Git(ui.RepoPath, "branch", "--list"));
    }

    [Fact]
    public async Task A_team_worktree_is_cut_in_its_own_repo_and_closing_one_repo_leaves_the_other()
    {
        var api = new GitWorkspace(NewRepo("api"), State, "Api");
        var ui = new GitWorkspace(NewRepo("ui"), State, "Ui");
        await api.EnsureIntegrationAsync("r1", "main", CancellationToken.None);
        await ui.EnsureIntegrationAsync("r1", "main", CancellationToken.None);
        var team = await ui.EnsureTeamAsync("r1", "team-1", CancellationToken.None);
        Assert.StartsWith(ui.IntegrationPath("r1")[..^"integration".Length], team);

        await api.RemoveRunAsync("r1", CancellationToken.None);

        Assert.False(Directory.Exists(api.IntegrationPath("r1")));
        Assert.True(Directory.Exists(ui.IntegrationPath("r1")));
        Assert.True(Directory.Exists(team));
    }

    [Fact]
    public async Task The_unnamed_workspace_keeps_the_single_repo_layout()
    {
        var single = new GitWorkspace(NewRepo("one"), State);
        Assert.Equal(Path.Combine(State, "worktrees", "r1", "integration"), single.IntegrationPath("r1"));
        await single.EnsureIntegrationAsync("r1", "main", CancellationToken.None);
        Assert.True(Directory.Exists(single.IntegrationPath("r1")));
    }

    [Fact]
    public async Task ForRepo_is_the_same_services_for_a_single_repo_run_or_an_unnamed_repo()
    {
        using var h = await TeamHarness.CreateAsync(new ScriptedChatClient(), new Limits(3, 2));
        Assert.Same(h.Services, h.Services.ForRepo(null));
        Assert.Same(h.Services, h.Services.ForRepo("Ui"));
    }
}
