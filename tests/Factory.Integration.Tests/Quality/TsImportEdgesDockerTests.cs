using Factory.S2.Quality;
using Factory.Harness.Sandbox;
using Xunit;

namespace Factory.Integration.Tests.Quality;

/// dependency-cruiser in the real runner image. Opt-in: FACTORY_TEST_DOCKER=1.
[Trait("needs", "docker")]
public class TsImportEdgesDockerTests
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("FACTORY_TEST_DOCKER") == "1";

    [Fact]
    public async Task Reads_the_imports_between_repo_files_and_ignores_packages_and_built_ins()
    {
        if (!Enabled) return;
        var root = Directory.CreateTempSubdirectory("depcruise-docker").FullName;
        Directory.CreateDirectory(Path.Combine(root, "web/src/domain")); Directory.CreateDirectory(Path.Combine(root, "web/src/infra"));
        File.WriteAllText(Path.Combine(root, "tsconfig.json"), "{\"compilerOptions\":{\"strict\":true}}");
        File.WriteAllText(Path.Combine(root, "web/src/infra/db.ts"), "export const db = 1;\n");
        File.WriteAllText(Path.Combine(root, "web/src/domain/rate.ts"), "import { db } from '../infra/db';\nimport fs from 'fs';\nexport const r = db + fs.length;\n");
        await using var sandbox = await new DockerSandboxFactory(new DockerSandboxOptions()).ForTeamAsync("depcruise", root, default);

        var edges = await TsImportEdges.ReadAsync(sandbox, root, default);

        var edge = Assert.Single(edges);
        Assert.Equal(("web/src/domain/rate.ts", "web/src/infra/db.ts"), (edge.Source, edge.Target));
    }
}
