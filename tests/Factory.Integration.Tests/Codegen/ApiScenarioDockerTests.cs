using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Harness;
using Factory.Harness.Registry;
using Factory.Harness.Sandbox;
using Factory.Integration.Tests.Support;
using Factory.S2;
using Xunit;

namespace Factory.Integration.Tests.Generation;

/// Opt-in (FACTORY_TEST_DOCKER=1): the real NSwag makes the real client, server interface and DTOs, the Factory writes the real API tests next to
/// them, and a tiny in-memory ASP.NET app with an adapter runs them: a right implementation passes, a wrong one fails naming the example and row.
/// NSwag runs in the runner image (docker/runner/build.sh; it must hold NSwag, so rebuild an image older than X2), or on the host with
/// FACTORY_API_LOCAL_NSWAG=1 when `nswag` 14.2.0 is on PATH. `dotnet test` runs on the host and restores xunit from NuGet, so it needs the network.
[Trait("needs", "docker")]
public sealed class ApiScenarioDockerTests
{
    private const string Yaml = """
        openapi: 3.0.0
        info: { title: Orders, version: "1" }
        paths:
          /orders/tags:
            post:
              operationId: tagOrders
              requestBody:
                required: true
                content: { application/json: { schema: { $ref: "#/components/schemas/TagRequest" } } }
              responses:
                "200": { description: tagged }
        components:
          schemas:
            TagRequest:
              type: object
              properties: { prefix: { type: string }, ref: { type: string }, tag: { type: string } }
        """;

    private const string Csproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net9.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><IsPackable>false</IsPackable></PropertyGroup>
          <ItemGroup>
            <FrameworkReference Include="Microsoft.AspNetCore.App" />
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
            <PackageReference Include="xunit" Version="2.9.2" />
            <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
          </ItemGroup>
        </Project>
        """;

    // The agent's part: the system under test (an in-memory orders store behind the generated server interface) and IScenarioAdapter.
    private const string Adapter = """
        using System.Text.Json.Nodes;
        using Contracts.ApiScenarios;
        using Contracts.Orders;
        using Microsoft.AspNetCore.Builder;
        using Microsoft.AspNetCore.Hosting;
        using Microsoft.Extensions.DependencyInjection;

        public sealed class OrdersStore { public List<JsonObject> Rows { get; } = []; }

        public sealed class Impl(OrdersStore store) : IOrdersController
        {
            public Task TagOrdersAsync(TagRequest body)
            {
                var wrong = Environment.GetEnvironmentVariable("ORDERS_IMPL") == "wrong";
                foreach (var row in store.Rows)
                {
                    var reference = row["ref"]?.GetValue<string>() ?? "";
                    var hit = body.Ref is not null ? reference == body.Ref : wrong || reference.StartsWith(body.Prefix ?? "", StringComparison.Ordinal);
                    if (hit) row["tag"] = body.Tag;
                }
                return Task.CompletedTask;
            }
        }

        public sealed class OrdersAdapter : IScenarioAdapter
        {
            private readonly OrdersStore _store = new();
            private WebApplication? _app;
            public async Task<HttpClient> StartAsync()
            {
                var b = WebApplication.CreateBuilder();
                b.WebHost.UseUrls("http://127.0.0.1:0");
                b.Services.AddControllers().AddApplicationPart(typeof(OrdersController).Assembly);
                b.Services.AddSingleton(_store);
                b.Services.AddSingleton<IOrdersController, Impl>();
                _app = b.Build();
                _app.MapControllers();
                await _app.StartAsync();
                return new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
            }
            public Task SeedAsync(string entity, IReadOnlyList<JsonObject> rows) { foreach (var r in rows) _store.Rows.Add((JsonObject)r.DeepClone()); return Task.CompletedTask; }
            public Task<IReadOnlyList<JsonObject>> QueryAsync(string entity) => Task.FromResult<IReadOnlyList<JsonObject>>(_store.Rows.ToList());
            public async ValueTask DisposeAsync() { if (_app is not null) await _app.DisposeAsync(); }
        }
        """;

    [Fact]
    public async Task Generated_api_tests_pass_against_a_right_implementation_and_name_the_row_against_a_wrong_one()
    {
        if (Environment.GetEnvironmentVariable("FACTORY_TEST_DOCKER") != "1") return;
        var work = Directory.CreateTempSubdirectory("api-scenarios-").FullName;
        var project = Directory.CreateTempSubdirectory("api-scenarios-proj-").FullName;
        try
        {
            var handover = (await new DesigncHandoverReader().LoadAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "handover"), new S2Config([]), default))
                .WithApiScenarios(ApiScenarioFixtures.Serving, ApiScenarioFixtures.Source, Yaml, [ApiScenarioFixtures.Scenario()], [ApiScenarioFixtures.Order()]);
            var targets = Codegen.targets(handover, null);
            Assert.True(targets.IsOk, string.Join("\n", targets.IsError ? targets.ErrorValue : []));
            Directory.CreateDirectory(Path.Combine(work, "in", "contracts", "openapi"));
            File.WriteAllText(Path.Combine(work, "in", ApiScenarioFixtures.Source), Yaml);

            ISandboxFactory sandboxes = Environment.GetEnvironmentVariable("FACTORY_API_LOCAL_NSWAG") == "1"
                ? new LocalSandboxFactory()
                : new DockerSandboxFactory(new DockerSandboxOptions(Image: Environment.GetEnvironmentVariable("FACTORY_RUNNER_IMAGE") ?? "factory-s2-runner:latest"));
            var run = await new SandboxCodeGenerator(sandboxes).GenerateAsync([.. targets.ResultValue], work, default);
            Assert.True(run.Outcome == "generated", run.Detail);

            var files = ApiScenarios.files(handover);
            Assert.True(files.IsOk, string.Join("\n", files.IsError ? files.ErrorValue : []));
            foreach (var (_, path, text) in files.ResultValue) Write(Path.Combine(work, "out", ".", path), text);

            // The generated tree and the agent's test project side by side, as in a repo.
            CopyDir(Path.Combine(work, "out", "."), project);
            File.WriteAllText(Path.Combine(project, "Orders.Tests.csproj"), Csproj);
            File.WriteAllText(Path.Combine(project, "Adapter.cs"), Adapter);

            var right = await TestAsync(project, "right");
            Assert.True(right.Succeeded, right.Tail(4000));
            Assert.Contains("Passed!", right.Output);

            var wrong = await TestAsync(project, "wrong");
            Assert.False(wrong.Succeeded, wrong.Tail(4000));
            Assert.Contains("ORD-1-ts-01 example 1 (prefix match): orders/Order id=2: tag expected null, got \"Priority\"", wrong.Output);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
            Directory.Delete(project, recursive: true);
        }
    }

    private static Task<ProcessResult> TestAsync(string project, string impl) =>
        ProcessRunner.ShellAsync($"dotnet test \"Orders.Tests.csproj\" --filter \"{ApiScenarios.filter}\" --nologo", project, TimeSpan.FromMinutes(10),
            new Dictionary<string, string> { ["ORDERS_IMPL"] = impl });

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static void CopyDir(string from, string to)
    {
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            Write(Path.Combine(to, Path.GetRelativePath(from, f)), File.ReadAllText(f));
    }
}
