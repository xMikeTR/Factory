using Factory.Core;
using Factory.Harness;
using Factory.S2.Quality;
using Factory.Integration.Tests.Support;
using FsCore = Factory.Core.Core;
using Xunit;

namespace Factory.Integration.Tests.Quality;

/// Namespace-level dependencies read from compiled assemblies (ArchUnitNET), as edges the Core fitness rules can judge.
public class NamespaceEdgesTests
{
    private static string Dll(Type t) => t.Assembly.Location;

    [Fact]
    public void Edges_are_namespace_paths_between_the_assemblies_given()
    {
        var edges = NamespaceEdges.Read([Dll(typeof(Factory.Harness.ProcessRunner)), Dll(typeof(Factory.Core.Fitness))]);

        Assert.Contains(edges, e => e.Source == "Factory/Harness/Sandbox" && e.Target == "Factory/Harness");
        Assert.Contains(edges, e => e.Source == "Factory/Harness/Policy" && e.Target == "Factory/Core");
    }

    [Fact]
    public void A_namespace_is_not_an_edge_to_itself_and_edges_are_distinct()
    {
        var edges = NamespaceEdges.Read([Dll(typeof(Factory.Harness.ProcessRunner))]);

        Assert.DoesNotContain(edges, e => e.Source == e.Target);
        Assert.Equal(edges.Count, edges.Distinct().Count());
    }

    [Fact]
    public void Dependencies_on_assemblies_not_given_are_not_edges()
    {
        var edges = NamespaceEdges.Read([Dll(typeof(Factory.Harness.ProcessRunner))]);

        Assert.DoesNotContain(edges, e => e.Target.StartsWith("System"));
    }

    private static ForbiddenDependency Rule(string from, string to) =>
        new(PathPatternModule.createOrFail(from), PathPatternModule.createOrFail(to), "layering");

    [Fact]
    public void A_forbidden_namespace_edge_is_reported_when_the_baseline_did_not_have_it()
    {
        var harness = Dll(typeof(Factory.Harness.ProcessRunner));
        var core = Dll(typeof(Factory.Core.Fitness));

        var found = NamespaceGate.Judge([Rule("Factory/Harness/Policy", "Factory/Core")], baseline: [core], current: [harness, core]);

        Assert.All(found, v => Assert.Equal(("Factory/Harness/Policy", "Factory/Core"), (v.Edge.Source, v.Edge.Target)));
        Assert.NotEmpty(found);
    }

    [Fact]
    public void A_forbidden_namespace_edge_the_baseline_already_had_is_debt()
    {
        var both = new[] { Dll(typeof(Factory.Harness.ProcessRunner)), Dll(typeof(Factory.Core.Fitness)) };

        Assert.Empty(NamespaceGate.Judge([Rule("Factory/Harness/Policy", "Factory/Core")], baseline: both, current: both));
    }

    [Fact]
    public void Cross_assembly_type_use_is_an_edge_between_the_projects_the_assemblies_were_built_from()
    {
        var edges = NamespaceEdges.ReadProjects([
            (Dll(typeof(Factory.Harness.ProcessRunner)), "src/Factory.Harness/Factory.Harness.csproj"),
            (Dll(typeof(Factory.Core.Fitness)), "src/Factory.Core/Factory.Core.fsproj")]);

        Assert.Contains(edges, e => e.Source == "src/Factory.Harness/Factory.Harness.csproj" && e.Target == "src/Factory.Core/Factory.Core.fsproj");
        Assert.DoesNotContain(edges, e => e.Source == "src/Factory.Core/Factory.Core.fsproj");
        Assert.Equal(edges.Count, edges.Distinct().Count());
    }

    private static ElementSpec El(string id, string area, string? repo = null) =>
        new(ElementId.NewElementId(id), id, FsCore.ToList([PathPatternModule.createOrFail(area)]), RiskLevel.Low, true,
            repo is null ? Microsoft.FSharp.Core.FSharpOption<string>.None : Microsoft.FSharp.Core.FSharpOption<string>.Some(repo));

    private static async Task<Handover> TwoElements(string? harnessRepo = null)
    {
        var baseHandover = await new Factory.Harness.Registry.DesigncHandoverReader().LoadAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "handover"), Factory.Harness.Registry.S2Config.Default, CancellationToken.None);
        return baseHandover.WithElements(El("harness", "src/Factory.Harness/**", harnessRepo), El("core", "src/Factory.Core/**", harnessRepo));
    }

    private static readonly (string, string) Harness = (Dll(typeof(Factory.Harness.ProcessRunner)), "src/Factory.Harness/Factory.Harness.csproj");
    private static readonly (string, string) CoreDll = (Dll(typeof(Factory.Core.Fitness)), "src/Factory.Core/Factory.Core.fsproj");

    [Fact]
    public async Task A_new_assembly_use_the_design_does_not_allow_is_a_violation_naming_both_elements()
    {
        var h = (await TwoElements()).WithDependencies(("harness", []), ("core", []));

        var found = NamespaceGate.JudgeAllowed(h, null, baseline: [CoreDll], current: [Harness, CoreDll]);

        var v = Assert.Single(found.New);
        Assert.Equal(("harness", "core", EdgeKind.AssemblyUse), (v.FromElement.Value, v.ToElement.Value, v.Kind));
        Assert.Empty(found.Debt);
        Assert.Contains("harness may not depend on core", AllowedEdges.line(v));
    }

    [Fact]
    public async Task An_assembly_use_the_baseline_already_had_is_debt_and_an_allowed_one_is_nothing()
    {
        var h = (await TwoElements()).WithDependencies(("harness", []), ("core", []));
        var both = new[] { Harness, CoreDll };

        var debt = NamespaceGate.JudgeAllowed(h, null, both, both);
        Assert.Empty(debt.New);
        Assert.Equal([(ElementId.NewElementId("harness"), ElementId.NewElementId("core"))], debt.Debt);

        var allowed = (await TwoElements()).WithDependencies(("harness", ["core"]), ("core", []));
        Assert.Empty(NamespaceGate.JudgeAllowed(allowed, null, [CoreDll], both).New);
    }

    [Fact]
    public async Task A_new_ts_import_the_design_does_not_allow_is_a_violation_and_an_old_one_is_debt()
    {
        var baseHandover = await new Factory.Harness.Registry.DesigncHandoverReader().LoadAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "handover"), Factory.Harness.Registry.S2Config.Default, CancellationToken.None);
        var h = baseHandover.WithElements(El("web", "web/**"), El("api", "api/**")).WithDependencies(("web", []), ("api", []));
        var old = new CodeEdge("web/a.ts", "api/x.ts");
        var added = new CodeEdge("web/b.ts", "api/y.ts");

        var fresh = TsGate.JudgeAllowed(h, null, [], [added]);
        Assert.Equal(("web", "api", EdgeKind.Import), (fresh.New.Single().FromElement.Value, fresh.New.Single().ToElement.Value, fresh.New.Single().Kind));
        var kept = TsGate.JudgeAllowed(h, null, [old], [old, added]);
        Assert.Empty(kept.New);
        Assert.Single(kept.Debt);
    }

    [Fact]
    public async Task Only_the_tasks_repo_owns_paths_when_a_repo_is_given()
    {
        var h = (await TwoElements("Api")).WithDependencies(("harness", []), ("core", []));
        Assert.Single(NamespaceGate.JudgeAllowed(h, "Api", [], [Harness, CoreDll]).New);
        Assert.Empty(NamespaceGate.JudgeAllowed(h, "Ui", [], [Harness, CoreDll]).New);
    }
}
