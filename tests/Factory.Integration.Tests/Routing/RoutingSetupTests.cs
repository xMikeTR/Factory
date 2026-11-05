using Factory.Core;
using Factory.Harness;
using Factory.Harness.Policy;
using Factory.Harness.Routing;
using Xunit;

namespace Factory.Integration.Tests.Routing;

public class RoutingSetupTests
{
    private static string Repo()
    {
        var repo = Path.Combine(Path.GetTempPath(), "s2-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        return repo;
    }

    private static ModelGateways Gateways(Dictionary<string, string> env) => ModelGateways.FromEnvironment(env, mergesAreDryRun: true);

    private static readonly Dictionary<string, string> Vertex = new()
    {
        ["FACTORY_PROVIDER"] = "vertex", ["FACTORY_VERTEX_PROJECT"] = "p", ["FACTORY_REVIEWER_SAME_FAMILY"] = "allow",
    };

    [Fact]
    public void Unset_or_env_means_no_routing_and_the_catalog_is_not_even_read()
    {
        var missingCatalog = new Dictionary<string, string>(Vertex) { ["FACTORY_CATALOG"] = "/nope.yaml" };

        Assert.Null(RoutingSetup.FromEnvironment(missingCatalog, Repo(), Gateways(missingCatalog)));
        var env = new Dictionary<string, string>(missingCatalog) { ["FACTORY_ROUTING"] = "env" };
        Assert.Null(RoutingSetup.FromEnvironment(env, Repo(), Gateways(env)));
    }

    [Fact]
    public void Catalog_loads_the_shipped_catalog_and_the_repo_routing_policy()
    {
        var env = new Dictionary<string, string>(Vertex) { ["FACTORY_ROUTING"] = "catalog" };

        var setup = RoutingSetup.FromEnvironment(env, Repo(), Gateways(env));

        Assert.NotNull(setup);
        Assert.NotEmpty(setup!.Catalog);
    }

    [Fact]
    public void An_unknown_mode_or_an_invalid_catalog_is_rejected_with_the_reasons()
    {
        var bogus = new Dictionary<string, string>(Vertex) { ["FACTORY_ROUTING"] = "magic" };
        Assert.Contains("env|catalog", Assert.Throws<FactoryRejectedException>(() => RoutingSetup.FromEnvironment(bogus, Repo(), Gateways(bogus)))!.Message);

        var bad = Path.Combine(Path.GetTempPath(), "bad-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(bad, "models:\n  - { id: x, modelName: m, tier: huge }\n");
        var env = new Dictionary<string, string>(Vertex) { ["FACTORY_ROUTING"] = "catalog", ["FACTORY_CATALOG"] = bad };
        var e = Assert.Throws<FactoryRejectedException>(() => RoutingSetup.FromEnvironment(env, Repo(), Gateways(env)));
        Assert.Contains("huge", e.Message);
    }
}
