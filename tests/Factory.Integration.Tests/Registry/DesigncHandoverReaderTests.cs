using Factory.Core;
using Factory.Harness.Registry;
using Xunit;

namespace Factory.Integration.Tests.Registry;

public class DesigncHandoverReaderTests
{
    private static readonly S2Config Config = new(["registry/**", ".github/**", "scripts/verify/**"]);

    private static string FixtureCopy()
    {
        var dir = Path.Combine(Path.GetTempPath(), "s2-handover-" + Guid.NewGuid().ToString("N"));
        CopyDir(Path.Combine(AppContext.BaseDirectory, "Fixtures", "handover"), dir);
        return dir;
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    private static Task<Handover> Load(string dir) => new DesigncHandoverReader().LoadAsync(dir, Config, CancellationToken.None);

    /// Designc copies scenarios/*.yaml into a handover (files: entry plus a `scenarios:` id list, `TODO` fitness entries included).
    /// The reader hashes it like any frozen file and ignores the list.
    [Fact]
    public async Task A_handover_with_scenarios_loads_and_a_changed_scenario_is_rejected()
    {
        var dir = FixtureCopy();
        const string scenario = "id: QAS-1\ntitle: Repo never written\nattribute: integrity\nfitness:\n  - \"TODO policy test\"\n";
        Directory.CreateDirectory(Path.Combine(dir, "scenarios"));
        await File.WriteAllTextAsync(Path.Combine(dir, "scenarios", "QAS-1.yaml"), scenario);
        var manifest = Path.Combine(dir, "handover.yaml");
        var text = await File.ReadAllTextAsync(manifest);
        text = text.Replace("evals: {}\n", "evals: {}\nscenarios: [\"QAS-1\"]\n")
            + $"  - {{ path: \"scenarios/QAS-1.yaml\", hash: {DesigncHandoverReader.Hash16(scenario)} }}\n";
        await File.WriteAllTextAsync(manifest, text);

        Assert.Equal("20261007-c4d072ba", (await Load(dir)).Id);

        await File.WriteAllTextAsync(Path.Combine(dir, "scenarios", "QAS-1.yaml"), scenario.Replace("TODO ", ""));
        await Assert.ThrowsAnyAsync<Exception>(() => Load(dir));
    }

    [Fact]
    public async Task Fixture_maps_to_the_expected_handover()
    {
        var h = await Load(FixtureCopy());

        Assert.Equal("20261007-c4d072ba", h.Id);
        Assert.StartsWith("sha256:", h.Digest);

        var quotes = Assert.Single(h.Elements, e => e.Name == "Quotes DB");
        Assert.Equal("Pricing Platform/Quotes DB", quotes.Id.Value);
        Assert.Equal(RiskLevel.High, quotes.Risk);
        Assert.Equal(["src/Pricing.Data/**", "db/migrations/**", "tests/Pricing.Data.Tests/**"], quotes.CodeAreas.Select(p => p.Value));
        Assert.Equal(
            ["Cat Model Adapter", "Pricing API", "Pricing Portal", "Quotes DB", "Rating Engine", "Referral Worker"],
            h.Elements.Select(e => e.Name).Order());
        Assert.Equal(RiskLevel.Low, h.Elements.Single(e => e.Name == "Pricing Portal").Risk);

        var d = Assert.Single(h.Decisions);
        Assert.Equal("D-0001", d.Id.Value);
        Assert.StartsWith("Quote writes use optimistic concurrency", d.Statement);
        Assert.Equal(["Pricing Platform/Quotes DB"], d.Binds.Select(b => b.Value));
        var check = Assert.Single(d.Checks);
        Assert.Equal(("p", "p check -tc tcOptimistic"), (check.Kind, check.Run));

        Assert.Equal(["registry/**", ".github/**", "scripts/verify/**"], h.ProtectedPaths.Select(p => p.Value));
        Assert.Equal(["eCommitted"], h.EventVocabulary);

        // Only relationships between elements that own code are kept (the person and the external vendor do not).
        var rels = h.Relationships.Select(r => (r.From.Value, r.To.Value)).ToList();
        Assert.Contains(("Pricing Platform/Pricing API", "Pricing Platform/Rating Engine"), rels);
        Assert.Contains(("Pricing Platform/Referral Worker", "Pricing Platform/Quotes DB"), rels);
        Assert.DoesNotContain(rels, r => r.Item1 == "Underwriter" || r.Item2 == "Cat Model Vendor");
    }

    [Fact]
    public async Task Digest_is_stable_for_the_same_package()
    {
        Assert.Equal((await Load(FixtureCopy())).Digest, (await Load(FixtureCopy())).Digest);
    }

    [Fact]
    public async Task Unknown_bound_element_is_rejected()
    {
        var dir = FixtureCopy();
        var path = Path.Combine(dir, "decisions", "D-0001.yaml");
        await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace("binds: [Quotes DB]", "binds: [Quotes Store]"));
        RewriteHash(dir, "decisions/D-0001.yaml");

        var ex = await Assert.ThrowsAsync<FactoryRejectedException>(() => Load(dir));
        Assert.Contains("D-0001 binds 'Quotes Store', which is not an element in the model", ex.Errors);
    }

    [Fact]
    public async Task A_file_changed_after_freezing_is_rejected()
    {
        var dir = FixtureCopy();
        await File.AppendAllTextAsync(Path.Combine(dir, "model", "workspace.dsl"), "\n// edited after freeze\n");

        var ex = await Assert.ThrowsAsync<FactoryRejectedException>(() => Load(dir));
        Assert.Contains(ex.Errors, e => e.StartsWith("model/workspace.dsl does not match its frozen hash"));
    }

    [Fact]
    public async Task A_handover_without_elements_is_rejected()
    {
        var dir = FixtureCopy();
        var manifest = Path.Combine(dir, "handover.yaml");
        File.WriteAllLines(manifest, File.ReadAllLines(manifest).Where(l => !l.StartsWith("elements:")));

        var ex = await Assert.ThrowsAsync<FactoryRejectedException>(() => Load(dir));
        Assert.Contains("handover.yaml names no elements file; re-freeze it with a designc that writes elements.json", ex.Errors);
    }

    [Theory]
    [InlineData("p check -tc tcOptimistic", "p")]
    [InlineData("designc check --only DC002", "lint")]
    [InlineData("./scripts/verify/two-writers.sh assert", "script")]
    public void Check_kind_comes_from_the_command_prefix(string run, string kind) =>
        Assert.Equal(kind, DesigncHandoverReader.CheckKind(run));

    private static void RewriteHash(string dir, string rel)
    {
        var manifest = Path.Combine(dir, "handover.yaml");
        var text = File.ReadAllText(Path.Combine(dir, rel));
        var lines = File.ReadAllLines(manifest).Select(l => l.Contains($"path: \"{rel}\"")
            ? $"  - {{ path: \"{rel}\", hash: {DesigncHandoverReader.Hash16(text)} }}" : l);
        File.WriteAllLines(manifest, lines);
    }

    [Fact]
    public async Task A_handover_yaml_that_is_not_yaml_is_rejected_not_crashed()
    {
        var dir = FixtureCopy();
        await File.WriteAllTextAsync(Path.Combine(dir, "handover.yaml"), "id: [unclosed\n");

        var e = await Assert.ThrowsAsync<FactoryRejectedException>(() => Load(dir));

        Assert.Contains(e.Errors, x => x.Contains("handover.yaml is not readable"));
    }

    /// Rewrites D-0001's checks and re-hashes it in the manifest, as a freeze would.
    private static async Task<string> WithChecks(string checksYaml)
    {
        var dir = FixtureCopy();
        var file = Path.Combine(dir, "decisions", "D-0001.yaml");
        var text = (await File.ReadAllTextAsync(file)).Replace("checks:\n  - p check -tc tcOptimistic\n", checksYaml);
        await File.WriteAllTextAsync(file, text);
        var manifest = Path.Combine(dir, "handover.yaml");
        await File.WriteAllTextAsync(manifest, (await File.ReadAllTextAsync(manifest)).Replace("83d766859d21cb09", DesigncHandoverReader.Hash16(text)));
        return dir;
    }

    /// designc records/1: a check is the command string, or {id, run} with id optional; a missing id is chk- + the first 8 hex of hash16(trimmed run).
    [Fact]
    public async Task A_check_is_a_command_or_an_id_and_run_and_every_check_gets_designcs_id()
    {
        var dir = await WithChecks("checks:\n  - p check -tc tcOptimistic\n  - { id: quotes.lock, run: \"p check -tc tcDedupe\" }\n  - { run: \"  dotnet test  \" }\n");

        var checks = Assert.Single((await Load(dir)).Decisions).Checks.ToList();

        Assert.Equal(["p check -tc tcOptimistic", "p check -tc tcDedupe", "dotnet test"], checks.Select(c => c.Run));
        Assert.Equal(["p", "p", "script"], checks.Select(c => c.Kind));
        Assert.Equal(
            ["chk-" + DesigncHandoverReader.Hash16("p check -tc tcOptimistic")[..8], "quotes.lock", "chk-" + DesigncHandoverReader.Hash16("dotnet test")[..8]],
            checks.Select(c => c.CheckId));
    }

    [Theory]
    [InlineData("checks:\n  - { id: x }\n")]                          // no run
    [InlineData("checks:\n  - { run: \"p check\", kind: p }\n")]        // a key records/1 does not allow
    [InlineData("checks:\n  - { id: \"has space\", run: \"p check\" }\n")] // id outside [A-Za-z0-9._-]
    public async Task A_check_object_that_is_not_id_and_run_rejects_the_handover(string checksYaml)
    {
        var dir = await WithChecks(checksYaml);

        var e = await Assert.ThrowsAsync<FactoryRejectedException>(() => Load(dir));
        Assert.Contains(e.Errors, m => m.Contains("D-0001") && m.Contains("check"));
    }
}
