using Factory.Core;
using Factory.S2.Verify;
using Xunit;

namespace Factory.Integration.Tests.Verify;

public class SuiteDiscoveryTests
{
    private const string TestProject = """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" /></ItemGroup></Project>""";

    private static string RepoWith(params (string Path, string Text)[] files)
    {
        var repo = Directory.CreateTempSubdirectory("s2-suite-").FullName;
        foreach (var (path, text) in files)
        {
            var full = Path.Combine(repo, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        return repo;
    }

    [Fact]
    public void A_named_suite_is_run_as_named()
    {
        Assert.Equal(SuitePlan.NewCommand("make test"), SuiteDiscovery.Plan("make test", RepoWith()));
    }

    /// GodotAgent-code: three test projects under one folder, no solution, build output left beside them.
    [Fact]
    public void With_none_named_every_test_project_on_disk_runs()
    {
        var repo = RepoWith(
            ("h/policy/tests/P.Tests/P.Tests.csproj", TestProject),
            ("h/policy/tests/P.Tests/bin/Debug/net9.0/P.Tests.csproj", TestProject),
            ("h/agent/src/A/A.csproj", "<Project />"),
            ("h/agent/tests/A.Tests/A.Tests.csproj", TestProject));

        var plan = Assert.IsType<SuitePlan.Command>(SuiteDiscovery.Plan(null, repo));

        Assert.Equal("s=0; for p in 'h/agent/tests/A.Tests/A.Tests.csproj' 'h/policy/tests/P.Tests/P.Tests.csproj'; do dotnet test \"$p\" || s=1; done; exit $s", plan.Item);
    }

    [Fact]
    public void A_solution_at_the_root_keeps_dotnet_test()
    {
        Assert.Equal(SuitePlan.NewCommand("dotnet test"), SuiteDiscovery.Plan(null, RepoWith(("App.slnx", "<Solution />"), ("T/T.csproj", TestProject))));
    }

    [Fact]
    public void A_typescript_package_on_disk_runs_npm_test_and_installed_packages_do_not()
    {
        var repo = RepoWith(
            ("web/package.json", """{ "scripts": { "test": "vitest run" } }"""),
            ("web/node_modules/vitest/package.json", """{ "scripts": { "test": "node test.js" } }"""));

        Assert.Equal(SuitePlan.NewCommand("s=0; (cd 'web' && npm test) || s=1; exit $s"), SuiteDiscovery.Plan(null, repo));
    }

    [Fact]
    public void An_empty_folder_has_no_suite()
    {
        Assert.Equal(SuitePlan.NoSuite, SuiteDiscovery.Plan(null, RepoWith()));
    }
}
