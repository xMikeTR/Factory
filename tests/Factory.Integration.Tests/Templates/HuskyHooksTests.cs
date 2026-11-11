using Factory.Integration.Tests.Support;
using Factory.Harness;
using Xunit;

namespace Factory.Integration.Tests.Templates;

/// Runs templates/husky against a throwaway git repo. `npm` is a stub on PATH: it logs the call and, when
/// STUB_REWRITE_LOCK is set, rewrites package-lock.json the way a drifting `npm i` would.
public class HuskyHooksTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("husky-repo-").FullName;
    private readonly string _bin = Directory.CreateTempSubdirectory("husky-bin-").FullName;

    private static string TemplateDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "templates", "husky"))) return Path.Combine(d.FullName, "templates", "husky");
        throw new DirectoryNotFoundException("templates/husky");
    }

    public HuskyHooksTests()
    {
        File.WriteAllText(Path.Combine(_bin, "npm"), "#!/bin/sh\necho \"npm $*\" >> \"$STUB_LOG\"\n[ -z \"${STUB_REWRITE_LOCK:-}\" ] || echo drift >> package-lock.json\n");
        Scripts.MakeExecutable(Path.Combine(_bin, "npm"));

        var hooks = Path.Combine(_repo, ".husky");
        foreach (var f in Directory.EnumerateFiles(TemplateDir(), "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".md")))
        {
            var to = Path.Combine(hooks, Path.GetRelativePath(TemplateDir(), f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
            Scripts.MakeExecutable(to);
        }
        Git("init", "-q", "-b", "main");
        Git("config", "core.hooksPath", ".husky");
        Git("config", "user.name", "t"); Git("config", "user.email", "t@t");
        File.WriteAllText(Path.Combine(_repo, "package.json"), "{\"name\":\"a\"}\n");
        File.WriteAllText(Path.Combine(_repo, "package-lock.json"), "lock-1\n");
        Commit("one");
        File.Delete(Log);
    }

    public void Dispose() { Directory.Delete(_repo, true); Directory.Delete(_bin, true); }

    private string Log => Path.Combine(_bin, "npm.log");

    private (int Exit, string Output) Git(params string[] args) => GitWith(null, args);

    private (int Exit, string Output) GitWith(string? rewriteLock, params string[] args)
    {
        var env = new Dictionary<string, string> { ["PATH"] = _bin + ":" + Environment.GetEnvironmentVariable("PATH"), ["STUB_LOG"] = Log };
        if (rewriteLock is not null) env["STUB_REWRITE_LOCK"] = rewriteLock;
        var r = ProcessRunner.RunAsync("git", args, _repo, TimeSpan.FromSeconds(30), env).GetAwaiter().GetResult();
        return (r.ExitCode, r.Tail(4000));
    }

    private void Commit(string message, params (string File, string Text)[] files)
    {
        foreach (var (f, t) in files) File.WriteAllText(Path.Combine(_repo, f), t);
        Git("add", "-A");
        Git("-c", "core.hooksPath=/dev/null", "commit", "-q", "-m", message);
    }

    private int Installs => File.Exists(Log) ? File.ReadAllLines(Log).Count(l => l == "npm i") : 0;

    [Fact]
    public void Checking_out_a_branch_that_changed_package_json_runs_npm_i()
    {
        Git("checkout", "-q", "-b", "feature");
        Commit("dep", ("package.json", "{\"name\":\"a\",\"x\":1}\n"), ("package-lock.json", "lock-2\n"));
        File.Delete(Log);

        var r = Git("checkout", "-q", "main");

        Assert.Equal(0, r.Exit);
        Assert.Equal(1, Installs);
    }

    [Fact]
    public void Checking_out_a_branch_with_no_package_changes_runs_nothing()
    {
        Git("checkout", "-q", "-b", "feature");
        Commit("code", ("a.txt", "x\n"));

        Git("checkout", "-q", "main");

        Assert.Equal(0, Installs);
    }

    [Fact]
    public void A_file_checkout_runs_nothing()
    {
        Commit("dep", ("package.json", "{\"name\":\"a\",\"x\":1}\n"));
        File.Delete(Log);

        Git("checkout", "HEAD~1", "--", "package.json");

        Assert.Equal(0, Installs);
    }

    [Fact]
    public void A_merge_that_brings_in_a_new_dependency_runs_npm_i()
    {
        Git("checkout", "-q", "-b", "feature");
        Commit("dep", ("package.json", "{\"name\":\"a\",\"x\":1}\n"), ("package-lock.json", "lock-2\n"));
        Git("checkout", "-q", "main");
        File.Delete(Log);

        var r = Git("merge", "-q", "--ff-only", "feature");

        Assert.Equal(0, r.Exit);
        Assert.Equal(1, Installs);
    }

    [Fact]
    public void Drift_in_the_lockfile_without_a_package_json_change_fails_loudly_and_restores_the_lockfile()
    {
        Git("checkout", "-q", "-b", "feature");
        Commit("lock only", ("package-lock.json", "lock-2\n"));
        Git("checkout", "-q", "main");
        File.Delete(Log);

        // merge brings in a lockfile-only change; the stub npm then rewrites the lockfile again
        var r = GitWith("1", "merge", "-q", "--ff-only", "feature");

        Assert.Equal("lock-2\n", File.ReadAllText(Path.Combine(_repo, "package-lock.json"))); // restored to what the merge produced
        Assert.Contains("package.json did not change", r.Output);
    }

    [Fact]
    public void A_lockfile_rewrite_is_fine_when_package_json_changed()
    {
        Git("checkout", "-q", "-b", "feature");
        Commit("dep", ("package.json", "{\"name\":\"a\",\"x\":1}\n"), ("package-lock.json", "lock-2\n"));
        Git("checkout", "-q", "main");

        var r = GitWith("1", "merge", "-q", "--ff-only", "feature");

        Assert.DoesNotContain("package.json did not change", r.Output);
        Assert.Contains("drift", File.ReadAllText(Path.Combine(_repo, "package-lock.json"))); // npm i's rewrite is kept
    }
}
