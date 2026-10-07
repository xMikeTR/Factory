using Factory.Harness.Sandbox;
using Xunit;

namespace Factory.Integration.Tests.Sandbox;

/// What every ICommandSandbox promises. Run against each implementation; Docker is opt-in
/// (FACTORY_TEST_DOCKER=1, image built from docker/runner).
public abstract class SandboxContract : IDisposable
{
    protected readonly string Worktree = Directory.CreateTempSubdirectory("sandbox-").FullName;
    protected abstract ISandboxFactory? Factory { get; }
    protected abstract string ExpectedKind { get; }
    private static readonly Dictionary<string, string> NoEnv = [];

    protected async Task<(ICommandSandbox Sandbox, bool Enabled)> SandboxAsync()
    {
        if (Factory is null) return (null!, false);
        return (await Factory.ForTeamAsync("team-contract", Worktree, default), true);
    }

    [Fact]
    public async Task Runs_in_the_worktree_and_reports_kind_output_and_exit_code()
    {
        var (s, on) = await SandboxAsync(); if (!on) return;
        await using var _ = s;
        await File.WriteAllTextAsync(Path.Combine(Worktree, "hello.txt"), "from host");

        var r = await s.RunAsync("pwd; cat hello.txt; exit 7", Worktree, TimeSpan.FromSeconds(30), NoEnv, default);

        Assert.Equal(ExpectedKind, s.Kind);
        Assert.Equal(7, r.ExitCode);
        Assert.Contains(Worktree, r.Output);
        Assert.Contains("from host", r.Output);
    }

    [Fact]
    public async Task Writes_land_in_the_worktree_owned_by_the_host_user()
    {
        var (s, on) = await SandboxAsync(); if (!on) return;
        await using var _ = s;

        var r = await s.RunAsync("mkdir -p out && echo built > out/x.txt", Worktree, TimeSpan.FromSeconds(30), NoEnv, default);

        Assert.True(r.Succeeded, r.Output);
        Assert.Equal("built\n", await File.ReadAllTextAsync(Path.Combine(Worktree, "out", "x.txt")));
        File.Delete(Path.Combine(Worktree, "out", "x.txt")); // the host user owns it
    }

    [Fact]
    public async Task Given_env_is_visible_and_a_canary_secret_on_the_host_is_not()
    {
        Environment.SetEnvironmentVariable("FACTORY_GATEWAY_KEY", "canary-7f3a-gateway");
        Environment.SetEnvironmentVariable("GH_TOKEN", "canary-7f3a-gh");
        var (s, on) = await SandboxAsync(); if (!on) return;
        await using var _ = s;

        var r = await s.RunAsync("env", Worktree, TimeSpan.FromSeconds(30), new Dictionary<string, string> { ["FACTORY_VERIFY_NS"] = "ns1" }, default);

        Assert.Contains("FACTORY_VERIFY_NS=ns1", r.Output);
        Assert.DoesNotContain("canary-7f3a", r.Output);
    }

    [Fact]
    public async Task A_command_past_its_timeout_is_killed_and_reported_timed_out()
    {
        var (s, on) = await SandboxAsync(); if (!on) return;
        await using var _ = s;

        var r = await s.RunAsync("sleep 60", Worktree, TimeSpan.FromSeconds(2), NoEnv, default);

        Assert.True(r.TimedOut);
        Assert.False(r.Succeeded);
        Assert.True(r.Duration < TimeSpan.FromSeconds(20), r.Duration.ToString());
    }

    public void Dispose()
    {
        try { Directory.Delete(Worktree, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed class LocalSandboxContract : SandboxContract
{
    protected override ISandboxFactory? Factory => new LocalSandboxFactory();
    protected override string ExpectedKind => "local";
}

[Trait("needs", "docker")]
public sealed class DockerSandboxContract : SandboxContract
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("FACTORY_TEST_DOCKER") == "1";
    private readonly Support.MemoryLedger _ledger = new();
    protected override ISandboxFactory? Factory => Enabled ? new DockerSandboxFactory(new DockerSandboxOptions(), _ledger, "run-contract") : null;
    protected override string ExpectedKind => "docker";

    [Fact]
    public async Task Outbound_network_is_refused()
    {
        var (s, on) = await SandboxAsync(); if (!on) return;
        await using var _ = s;

        var r = await s.RunAsync("timeout 5 bash -c 'echo > /dev/tcp/1.1.1.1/443' && echo CONNECTED", Worktree, TimeSpan.FromSeconds(20),
            new Dictionary<string, string>(), default);

        Assert.DoesNotContain("CONNECTED", r.Output);
        Assert.NotEqual(0, r.ExitCode);
    }

    [Fact]
    public async Task The_restore_window_has_network_and_closes_before_commands_run()
    {
        await File.WriteAllTextAsync(Path.Combine(Worktree, "probe.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
            <ItemGroup><PackageReference Include="Newtonsoft.Json" Version="13.0.3" /></ItemGroup></Project>
            """);
        var (s, on) = await SandboxAsync(); if (!on) return;
        await using var _ = s;

        var restore = Assert.IsType<DockerSandbox>(s).Restore;
        Assert.NotNull(restore);
        Assert.True(restore!.Succeeded, restore.Output);
        var row = Assert.Single(_ledger.Events, e => e.Stage == "restore");
        Assert.Equal(("run-contract", "team-contract", "passed"), (row.RunId, row.TeamId, row.Outcome));
        Assert.Contains("\"sandbox\":\"docker\"", row.Payload);
        var build = await s.RunAsync("dotnet build --no-restore -nologo -v q", Worktree, TimeSpan.FromMinutes(3), new Dictionary<string, string>(), default);
        Assert.True(build.Succeeded, build.Output);
        var r = await s.RunAsync("timeout 5 bash -c 'echo > /dev/tcp/1.1.1.1/443' && echo CONNECTED", Worktree, TimeSpan.FromSeconds(20),
            new Dictionary<string, string>(), default);
        Assert.DoesNotContain("CONNECTED", r.Output);
    }

    /// A change off the allow-list stops the window before the network opens; the ledger says why.
    [Fact]
    public async Task A_refused_restore_opens_no_network()
    {
        await File.WriteAllTextAsync(Path.Combine(Worktree, "probe.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
            <Target Name="X" BeforeTargets="Restore"><Exec Command="curl https://example.com" /></Target></Project>
            """);
        var (s, on) = await SandboxAsync(); if (!on) return;
        await using var _ = s;

        Assert.Null(Assert.IsType<DockerSandbox>(s).Restore);
        Assert.Contains(s.RestoreRefusal!, r => r.StartsWith("probe.csproj:") && r.Contains("Target"));
        var row = Assert.Single(_ledger.Events, e => e.Stage == "restore");
        Assert.Equal("refused", row.Outcome);
        Assert.Contains("Target", row.Payload);
    }

    /// The window restores from the image's pinned config, nuget.org only.
    [Fact]
    public async Task The_image_pins_package_sources_to_nuget_org()
    {
        var (s, on) = await SandboxAsync(); if (!on) return;
        await using var _ = s;

        var r = await s.RunAsync($"cat {RestoreGate.ConfigFile}", Worktree, TimeSpan.FromSeconds(20), new Dictionary<string, string>(), default);

        Assert.True(r.Succeeded, r.Output);
        Assert.Contains("<clear />", r.Output);
        Assert.Contains(RestoreGate.NuGetOrg, r.Output);
    }

    /// Decision checks are P models: `p compile` emits a net8.0 project, which must build with
    /// the network off (live1 failed here with NU1301 fetching the .NET 8 packs).
    [Fact]
    public async Task A_P_model_compiles_and_checks_with_the_network_off()
    {
        var (s, on) = await SandboxAsync(); if (!on) return;
        await using var _ = s;
        var p = Path.Combine(Worktree, "P");
        Directory.CreateDirectory(Path.Combine(p, "PSrc"));
        Directory.CreateDirectory(Path.Combine(p, "PTst"));
        await File.WriteAllTextAsync(Path.Combine(p, "Probe.pproj"), """
            <Project><ProjectName>Probe</ProjectName><InputFiles><PFile>./PSrc/</PFile><PFile>./PTst/</PFile></InputFiles><OutputDir>./PGenerated/</OutputDir></Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(p, "PSrc", "Probe.p"), "machine Probe { start state Init { entry { assert true; } } }\n");
        await File.WriteAllTextAsync(Path.Combine(p, "PTst", "Test.p"), "test tcProbe [main=Probe]: { Probe };\n");

        var r = await s.RunAsync("p compile && p check -tc tcProbe -s 5", p, TimeSpan.FromMinutes(5), new Dictionary<string, string>(), default);

        Assert.True(r.Succeeded, r.Output);
        Assert.DoesNotContain("NU1301", r.Output);
    }
}
