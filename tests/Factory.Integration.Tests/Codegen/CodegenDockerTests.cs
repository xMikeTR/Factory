using Factory.Core;
using Factory.Harness.Sandbox;
using Factory.S2;
using Xunit;

namespace Factory.Integration.Tests.Generation;

/// Opt-in (FACTORY_TEST_DOCKER=1, runner image built with docker/runner/build.sh): the real NSwag in the real image makes real C#.
[Trait("needs", "docker")]
public sealed class CodegenDockerTests
{
    [Fact]
    public async Task NSwag_in_the_runner_image_generates_the_dtos_server_and_client_offline()
    {
        if (Environment.GetEnvironmentVariable("FACTORY_TEST_DOCKER") != "1") return;
        var work = Directory.CreateTempSubdirectory("nswag-").FullName;
        try
        {
            const string yaml = """
                openapi: 3.0.0
                info: { title: Fares, version: "1" }
                paths:
                  /fares/{id}:
                    get:
                      operationId: getFare
                      parameters: [{ name: id, in: path, required: true, schema: { type: string } }]
                      responses:
                        "200": { description: ok, content: { application/json: { schema: { $ref: "#/components/schemas/Fare" } } } }
                components:
                  schemas:
                    Fare: { type: object, properties: { amount: { type: number } } }
                """;
            Directory.CreateDirectory(Path.Combine(work, "in", "contracts"));
            File.WriteAllText(Path.Combine(work, "in", "contracts", "fares.yaml"), yaml);
            var target = new Codegen.Target(".", "contracts/fares.yaml", "Fares", "Contracts.Fares", Microsoft.FSharp.Collections.ListModule.OfSeq([Codegen.Side.Server, Codegen.Side.Client]),
                Microsoft.FSharp.Collections.ListModule.OfSeq(["generated/contracts/csharp/Fares/Fares.Dtos.g.cs", "generated/contracts/csharp/Fares/Fares.Server.g.cs", "generated/contracts/csharp/Fares/Fares.Client.g.cs"]));
            var run = await new SandboxCodeGenerator(new DockerSandboxFactory(new DockerSandboxOptions(Image: Environment.GetEnvironmentVariable("FACTORY_RUNNER_IMAGE") ?? "factory-s2-runner:latest"))).GenerateAsync([target], work, default);
            Assert.True(run.Outcome == "generated", run.Detail);
            foreach (var f in target.Files) Assert.True(File.Exists(Path.Combine(work, Codegen.outPath(".", f))), f);
        }
        finally { Directory.Delete(work, recursive: true); }
    }
}
