using Factory.Core;
using Factory.Harness.Policy;
using Microsoft.Extensions.AI;

namespace Factory.Harness.Routing;

/// Everything catalog routing needs, built once at start-up: the model catalog, the repo's routing policy, and how a
/// catalog row becomes a client. Null (no routing) unless FACTORY_ROUTING=catalog; `env` (the default) leaves every
/// model choice on the FACTORY_MODEL_* variables exactly as before.
public sealed record RoutingSetup(IReadOnlyList<ModelEntry> Catalog, RoutingPolicyFile.RoutingConfig Config, Func<ModelEntry, IChatClient> ClientFor)
{
    public static RoutingSetup? FromEnvironment(IReadOnlyDictionary<string, string> env, string repoPath, ModelGateways gateways)
    {
        var mode = env.TryGetValue("FACTORY_ROUTING", out var m) && m.Length > 0 ? m : "env";
        switch (mode)
        {
            case "env": return null;
            case "catalog":
                var catalog = CatalogFile.Load(env);
                if (catalog.Errors.Count > 0) throw new FactoryRejectedException("the model catalog is invalid", [.. catalog.Errors]);
                return new RoutingSetup(catalog.Models, RoutingPolicyFile.Load(repoPath), gateways.ChatClientFor);
            default:
                throw new FactoryRejectedException("FACTORY_ROUTING is invalid", [$"'{mode}' is not env|catalog"]);
        }
    }
}
