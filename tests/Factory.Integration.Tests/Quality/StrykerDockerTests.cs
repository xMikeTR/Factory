using Factory.Harness;
using Factory.Harness.Sandbox;
using Factory.S2.Quality;
using Xunit;

namespace Factory.Integration.Tests.Quality;

/// Stryker.NET in the real runner image, offline after the restore window. Opt-in: FACTORY_TEST_DOCKER=1.
[Trait("needs", "docker")]
public class StrykerDockerTests
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("FACTORY_TEST_DOCKER") == "1";

    private static async Task Git(string dir, params string[] args) =>
        Assert.True((await ProcessRunner.RunAsync("git", args, dir, TimeSpan.FromSeconds(30))).Succeeded);

    private static async Task<string> SampleRepoAsync()
    {
        var root = Directory.CreateTempSubdirectory("stryker-docker").FullName;
        Directory.CreateDirectory(Path.Combine(root, "src")); Directory.CreateDirectory(Path.Combine(root, "tests"));
        File.WriteAllText(Path.Combine(root, "src/Lib.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(root, "src/Calc.cs"), "namespace L; public static class Calc { public static int Add(int a, int b) => a + b; }");
        File.WriteAllText(Path.Combine(root, "tests/T.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework><IsPackable>false</IsPackable></PropertyGroup>
            <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" /><PackageReference Include="xunit" Version="2.9.2" /><PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" /></ItemGroup>
            <ItemGroup><ProjectReference Include="../src/Lib.csproj" /></ItemGroup></Project>
            """);
        File.WriteAllText(Path.Combine(root, "tests/CalcTests.cs"), "using Xunit; public class CalcTests { [Fact] public void Adds() => Assert.Equal(5, L.Calc.Add(2, 3)); }");
        await ProcessRunner.RunAsync("dotnet", ["new", "sln", "-n", "App"], root, TimeSpan.FromSeconds(60));
        await ProcessRunner.RunAsync("dotnet", ["sln", "add", "src/Lib.csproj", "tests/T.csproj"], root, TimeSpan.FromSeconds(60));
        await Git(root, "init", "-q"); await Git(root, "add", "-A");
        await ProcessRunner.RunAsync("git", ["-c", "user.name=t", "-c", "user.email=t@t", "commit", "-qm", "init"], root, TimeSpan.FromSeconds(30));
        return root;
    }

    [Fact]
    public async Task Collects_coverage_from_the_test_project_offline_and_joins_it_onto_functions()
    {
        if (!Enabled) return;
        var root = await SampleRepoAsync();
        await using var sandbox = await new DockerSandboxFactory(new DockerSandboxOptions()).ForTeamAsync("cov", root, default);

        var reports = await CoverageRunner.CollectAsync(sandbox, root, default);

        var fn = new Factory.Core.FunctionMetrics("src/Calc.cs", "Add", 1, 1, 1, Microsoft.FSharp.Core.FSharpOption<double>.None);
        var joined = Assert.Single(Cobertura.Join([fn], reports));
        Assert.Equal(1.0, joined.Coverage.Value);
    }

    [Fact]
    public async Task Mutates_a_changed_file_and_finds_the_mutant_a_weak_test_misses()
    {
        if (!Enabled) return;
        var root = Directory.CreateTempSubdirectory("stryker-docker").FullName;
        Directory.CreateDirectory(Path.Combine(root, "src")); Directory.CreateDirectory(Path.Combine(root, "tests"));
        File.WriteAllText(Path.Combine(root, "src/Lib.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(root, "src/Calc.cs"), "namespace L; public static class Calc { public static int Add(int a, int b) => a + b; }");
        File.WriteAllText(Path.Combine(root, "tests/T.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework><IsPackable>false</IsPackable></PropertyGroup>
            <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" /><PackageReference Include="xunit" Version="2.9.2" /><PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" /></ItemGroup>
            <ItemGroup><ProjectReference Include="../src/Lib.csproj" /></ItemGroup></Project>
            """);
        File.WriteAllText(Path.Combine(root, "tests/CalcTests.cs"), "using Xunit; public class CalcTests { [Fact] public void Adds() => Assert.Equal(5, L.Calc.Add(2, 3)); }");
        await ProcessRunner.RunAsync("dotnet", ["new", "sln", "-n", "App"], root, TimeSpan.FromSeconds(60));
        await ProcessRunner.RunAsync("dotnet", ["sln", "add", "src/Lib.csproj", "tests/T.csproj"], root, TimeSpan.FromSeconds(60));
        await Git(root, "init", "-q"); await Git(root, "add", "-A");
        await ProcessRunner.RunAsync("git", ["-c", "user.name=t", "-c", "user.email=t@t", "commit", "-qm", "init"], root, TimeSpan.FromSeconds(30));

        var run = await new StrykerRunner(new DockerSandboxFactory(new DockerSandboxOptions())).RunAsync(root, ["src/Calc.cs"], default);

        Assert.False(run.TimedOut);
        Assert.NotEmpty(run.Mutants);
        Assert.All(run.Mutants, m => Assert.Equal("src/Calc.cs", m.File));
        Assert.Contains(run.Mutants, m => m.Status.IsKilled);
    }

    [Fact]
    public async Task A_qa_environment_service_is_reachable_by_name_from_the_sandbox_and_removed_with_it()
    {
        if (!Enabled) return;
        var root = await SampleRepoAsync();
        var qa = new Factory.Harness.Policy.QaEnv([new Factory.Harness.Policy.QaService("db", "factory-s2-runner:latest")], []);
        string network;
        await using (var env = await QaEnvironment.StartAsync(qa, new DockerSandboxOptions(), default))
        {
            Assert.Equal("db", env.Env["QA_DB_HOST"]);
            await using var sandbox = await env.Sandboxes.ForTeamAsync("qa-net", root, default);
            var found = await sandbox.RunAsync("getent hosts db", root, TimeSpan.FromSeconds(30), new Dictionary<string, string>(), default);
            Assert.Equal(0, found.ExitCode);
            var nets = await ProcessRunner.RunAsync("docker", ["network", "ls", "--format", "{{.Name}}", "--filter", "name=s2-qa-"], root, TimeSpan.FromSeconds(30));
            network = nets.Output.Trim();
            Assert.NotEmpty(network);
        }
        var after = await ProcessRunner.RunAsync("docker", ["network", "ls", "--format", "{{.Name}}", "--filter", "name=s2-qa-"], root, TimeSpan.FromSeconds(30));
        Assert.DoesNotContain(network.Split('\n')[0], after.Output);
    }

    [Fact]
    public async Task Eslint_in_the_image_measures_the_complexity_of_a_ts_function_offline()
    {
        if (!Enabled) return;
        var root = Directory.CreateTempSubdirectory("eslint-docker").FullName;
        Directory.CreateDirectory(Path.Combine(root, "web"));
        File.WriteAllText(Path.Combine(root, "web/a.ts"), "export function pick(a: number, b: number): number {\n  if (a > b) { return a; }\n  return b > 0 && a > 0 ? b : 0;\n}\n");
        await using var sandbox = await new DockerSandboxFactory(new DockerSandboxOptions()).ForTeamAsync("eslint", root, default);

        var fns = await EslintRunner.MeasureAsync(sandbox, root, "web/a.ts", null, default);

        var pick = Assert.Single(fns);
        Assert.Equal("web/a.ts", pick.File);
        Assert.True(pick.Cyclomatic >= 4, $"complexity {pick.Cyclomatic}");
    }

    [Fact]
    public async Task A_namespace_rule_is_judged_on_assemblies_built_in_the_sandbox()
    {
        if (!Enabled) return;
        var root = await SampleRepoAsync();
        File.WriteAllText(Path.Combine(root, "src/Calc.cs"), "namespace L.Inner { public static class Helper { public static int One() => 1; } }\nnamespace L { public static class Calc { public static int Add(int a, int b) => a + b + L.Inner.Helper.One() - 1; } }");
        var dlls = await NamespaceGate.BuildAsync(new DockerSandboxFactory(new DockerSandboxOptions()), "arch", root, default);

        Assert.Contains(dlls, d => d.EndsWith("Lib.dll"));
        var rule = new Factory.Core.ForbiddenDependency(Factory.Core.PathPatternModule.createOrFail("L"), Factory.Core.PathPatternModule.createOrFail("L/Inner"), "the facade must not reach inward");
        var found = Assert.Single(NamespaceGate.Judge([rule], baseline: [], current: dlls));
        Assert.Equal(("L", "L/Inner"), (found.Edge.Source, found.Edge.Target));
    }
}
