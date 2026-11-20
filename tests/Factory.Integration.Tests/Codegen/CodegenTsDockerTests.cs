using Factory.Core;
using Factory.Harness.Sandbox;
using Factory.S2;
using Xunit;

namespace Factory.Integration.Tests.Generation;

/// Opt-in (FACTORY_TEST_DOCKER=1, filter CodegenTsDocker): the pinned openapi-typescript in the runner image writes a schema offline, byte-identical
/// from different work dirs; the schema plus the Factory-rendered client type-check under `tsc --strict` in the sandbox, and a wrong argument type does not.
/// FACTORY_RUNNER_IMAGE names the image (default factory-s2-runner:latest); any image with Node 22, typescript@5 and the pinned tool works.
[Trait("needs", "docker")]
public sealed class CodegenTsDockerTests
{
    private const string Yaml = """
        openapi: 3.0.0
        info: { title: Shop, version: "1" }
        paths:
          /orders/{id}:
            get:
              operationId: getOrder
              parameters: [{ name: id, in: path, required: true, schema: { type: string } }]
              responses:
                "200": { description: ok, content: { application/json: { schema: { $ref: "#/components/schemas/Order" } } } }
          /orders:
            post:
              operationId: createOrder
              requestBody: { required: true, content: { application/json: { schema: { $ref: "#/components/schemas/Order" } } } }
              responses:
                "201": { description: created, content: { application/json: { schema: { $ref: "#/components/schemas/Order" } } } }
        components:
          schemas:
            Order: { type: object, required: [id], properties: { id: { type: string }, total: { type: number } } }
        """;

    private const string Tsc = "tsc --noEmit --strict --noUnusedLocals --noUnusedParameters --exactOptionalPropertyTypes --target es2022 --lib es2022,dom --module esnext --moduleResolution bundler";

    private static Codegen.Target Target()
    {
        var h = new Codegen.ClientOp[] { new("createOrder", "POST", "/orders"), new("getOrder", "GET", "/orders/{id}") };
        return new Codegen.Target(".", "contracts/shop.yaml", "Shop", "", Microsoft.FSharp.Collections.FSharpList<Codegen.Side>.Empty,
            Microsoft.FSharp.Collections.ListModule.OfSeq(["generated/contracts/ts/Shop/Shop.schema.ts", "generated/contracts/ts/Shop/Shop.client.ts"]),
            Codegen.Language.TypeScript, Microsoft.FSharp.Collections.ListModule.OfSeq(h));
    }

    private static async Task<string> GenerateAsync(string work, DockerSandboxFactory sandboxes)
    {
        Directory.CreateDirectory(Path.Combine(work, "in", "contracts"));
        File.WriteAllText(Path.Combine(work, "in", "contracts", "shop.yaml"), Yaml);
        var run = await new SandboxCodeGenerator(sandboxes).GenerateAsync([Target()], work, default);
        Assert.True(run.Outcome == "generated", run.Detail);
        var schema = Path.Combine(work, Codegen.outPath(".", "generated/contracts/ts/Shop/Shop.schema.ts"));
        return await File.ReadAllTextAsync(schema);
    }

    [Fact]
    public async Task Openapi_typescript_is_pinned_deterministic_and_the_client_type_checks_offline()
    {
        if (Environment.GetEnvironmentVariable("FACTORY_TEST_DOCKER") != "1") return;
        var image = Environment.GetEnvironmentVariable("FACTORY_RUNNER_IMAGE") ?? "factory-s2-runner:latest";
        var sandboxes = new DockerSandboxFactory(new DockerSandboxOptions(Image: image, RestoreOnStart: false));
        var a = Directory.CreateTempSubdirectory("ots-a-").FullName;
        var b = Directory.CreateTempSubdirectory("ots-b-").FullName;
        try
        {
            var schemaA = await GenerateAsync(a, sandboxes);
            Assert.Equal(schemaA, await GenerateAsync(b, sandboxes)); // different work dirs, same bytes

            var dir = Path.Combine(a, "out", ".");
            var client = Path.Combine(dir, "generated", "contracts", "ts", "Shop", "Shop.client.ts");
            File.WriteAllText(client, Codegen.tsClient(Target(), new string('a', 64)));
            var good = "import { createShopClient } from \"./generated/contracts/ts/Shop/Shop.client\";\n" +
                       "export const run = () => createShopClient(\"http://x\").getOrder({ path: { id: \"1\" } });\n" +
                       "export const make = () => createShopClient(\"http://x\").createOrder({ body: { id: \"1\" } });\n";
            var bad = good.Replace("id: \"1\"", "id: 1");
            File.WriteAllText(Path.Combine(dir, "good.ts"), good);
            File.WriteAllText(Path.Combine(dir, "bad.ts"), bad);

            await using var sandbox = await sandboxes.ForTeamAsync("ots", dir, default);
            var version = await sandbox.RunAsync("openapi-typescript --version", dir, TimeSpan.FromMinutes(1), new Dictionary<string, string>(), default);
            Assert.Contains(Codegen.openapiTypescriptVersion, version.Output);
            var ok = await sandbox.RunAsync($"{Tsc} good.ts", dir, TimeSpan.FromMinutes(2), new Dictionary<string, string>(), default);
            Assert.True(ok.Succeeded, ok.Tail(2000));
            var nope = await sandbox.RunAsync($"{Tsc} bad.ts", dir, TimeSpan.FromMinutes(2), new Dictionary<string, string>(), default);
            Assert.False(nope.Succeeded, "a number where the path id is a string must not type-check");
        }
        finally { Directory.Delete(a, recursive: true); Directory.Delete(b, recursive: true); }
    }
}
