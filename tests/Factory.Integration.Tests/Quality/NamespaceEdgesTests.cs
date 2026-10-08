using Factory.Core;
using Factory.Harness;
using Factory.S2.Quality;
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
}
