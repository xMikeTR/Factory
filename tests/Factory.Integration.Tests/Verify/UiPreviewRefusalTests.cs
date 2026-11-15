using Factory.Harness.Sandbox;
using Factory.S2.Verify;
using Xunit;

namespace Factory.Integration.Tests.Verify;

/// What `factory-s2 preview` refuses before any sandbox starts. Runs designc's python (not Docker); does nothing when no designc checkout is found.
public class UiPreviewRefusalTests
{
    private sealed class NoSandbox : ISandboxFactory
    {
        public bool Started;
        public Task<ICommandSandbox> ForTeamAsync(string teamId, string worktree, CancellationToken ct) { Started = true; throw new InvalidOperationException("no sandbox expected"); }
    }

    private static UiPreviewRequest Request(UiPreviewFixture f, string designc, string screen = "bordereaux") =>
        new(f.Registry, screen, f.Repo, designc, "factory-s2-runner:latest");

    [Fact]
    public async Task A_screen_with_no_spec_is_refused_with_exit_2()
    {
        if (UiPreviewFixture.DesigncDir() is not { } designc) return;
        var f = await UiPreviewFixture.CreateAsync();
        var sandboxes = new NoSandbox();

        var result = await new UiPreviewRunner(sandboxes).RunAsync(Request(f, designc, "missing"), default);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("no spec for 'missing'", result.Message);
        Assert.False(sandboxes.Started);
    }

    [Fact]
    public async Task A_spec_designc_fails_is_refused_with_its_clauses()
    {
        if (UiPreviewFixture.DesigncDir() is not { } designc) return;
        var f = await UiPreviewFixture.CreateAsync();
        var spec = Path.Combine(f.Registry, "ui", "specs", "bordereaux.json");
        File.WriteAllText(spec, File.ReadAllText(spec).Replace("\"component\": \"DataTable\"", "\"component\": \"NoSuchComponent\""));
        var sandboxes = new NoSandbox();

        var result = await new UiPreviewRunner(sandboxes).RunAsync(Request(f, designc), default);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("NoSuchComponent", result.Message);
        Assert.False(sandboxes.Started);
        Assert.False(Directory.Exists(Path.Combine(f.Registry, "ui", "previews")));
    }

    [Fact]
    public async Task A_dirty_repo_is_refused_with_exit_2()
    {
        if (UiPreviewFixture.DesigncDir() is not { } designc) return;
        var f = await UiPreviewFixture.CreateAsync();
        File.AppendAllText(Path.Combine(f.Repo, "vitest.config.ts"), "\n// edited\n");
        var sandboxes = new NoSandbox();

        var result = await new UiPreviewRunner(sandboxes).RunAsync(Request(f, designc), default);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("uncommitted", result.Message);
        Assert.False(sandboxes.Started);
    }

    [Fact]
    public async Task A_screen_name_that_escapes_the_registry_is_refused()
    {
        var f = await UiPreviewFixture.CreateAsync();
        var sandboxes = new NoSandbox();

        var result = await new UiPreviewRunner(sandboxes).RunAsync(Request(f, "/nowhere", "../x"), default);

        Assert.Equal(2, result.ExitCode);
        Assert.False(sandboxes.Started);
    }

    [Fact]
    public async Task A_laptop_sandbox_is_refused_because_a_render_outside_the_runner_image_can_never_be_approved()
    {
        if (UiPreviewFixture.DesigncDir() is not { } designc) return;
        var f = await UiPreviewFixture.CreateAsync();

        var result = await new UiPreviewRunner(new LocalSandboxFactory()).RunAsync(Request(f, designc), default);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("runner image", result.Message);
        Assert.False(Directory.Exists(Path.Combine(f.Registry, "ui", "previews")));
        Assert.Empty((await new Factory.S2.Git.GitWorkspace(f.Repo, Path.Combine(f.Repo, ".factory")).ChangedPathsAsync(f.Repo, default)));
    }
}
