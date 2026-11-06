using Factory.Core;
using Factory.Harness;
using Factory.Harness.Policy;
using Factory.Harness.Registry;
using Factory.Harness.Routing;
using Factory.Integration.Tests.Support;
using Factory.S2;
using Microsoft.Extensions.AI;
using Xunit;
using static Factory.Integration.Tests.Support.ScriptedChatClient;

namespace Factory.Integration.Tests.Routing;

public class RoutedAgentsTests
{
    private const string Catalog = """
        models:
          - { id: c-small, modelName: claude-haiku-4-5, tier: small }
          - { id: c-std, modelName: claude-sonnet-5-5, tier: standard }
          - { id: c-top, modelName: claude-opus-5-5, tier: frontier }
          - { id: g-small, family: gemini, modelName: g-flash, tier: small }
          - { id: g-std, family: gemini, modelName: g-mid, tier: standard }
          - { id: g-top, family: gemini, modelName: g-pro, tier: frontier }
        """;

    private readonly MemoryLedger _ledger = new();

    private static async Task<RouteTable> TableAsync()
    {
        var h = await new DesigncHandoverReader().LoadAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "handover"), S2Config.Default, CancellationToken.None);
        return RunRouter.Route("run-1", h, [Plans.Task("T-001")], CatalogFile.Parse(Catalog).Models, RoutingPolicyFile.Load(Path.Combine(Path.GetTempPath(), "none")));
    }

    private sealed class Boom : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken c = default) => throw new InvalidOperationException("env model used");
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken c = default) => throw new InvalidOperationException("env model used");
        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task A_routed_pair_uses_the_client_built_for_its_ladder_entry_not_the_env_model()
    {
        var table = await TableAsync();
        var built = new List<string>();
        var agents = new FactoryAgents(_ => new Boom(), new RoleAllowListPolicy(), _ledger);
        agents.UseRouting(table, entry => { built.Add(entry.Id); return new ScriptedChatClient(Say("ok")); });

        var agent = agents.Create(new AgentScope("run-1", Roles.Implementer, "team-T-001", "T-001"), ModelTier.Standard, "x", []);
        await agent.RunAsync("go");

        Assert.Equal(table.Find("T-001", Roles.Implementer)![ModelTier.Standard].Id, Assert.Single(built));
    }

    [Fact]
    public async Task A_pair_that_was_not_routed_keeps_the_env_model()
    {
        var table = await TableAsync();
        var env = new ScriptedChatClient(Say("ok"), Say("ok"));
        var agents = new FactoryAgents(_ => env, new RoleAllowListPolicy(), _ledger);
        agents.UseRouting(table, _ => throw new InvalidOperationException("routed client used"));

        await agents.Create(new AgentScope("run-1", Roles.Planner), ModelTier.Frontier, "x", []).RunAsync("go");
        await agents.Create(new AgentScope("run-1", Roles.Implementer, "team-T-9", "T-9"), ModelTier.Standard, "x", []).RunAsync("go");

        Assert.Equal(2, env.Requests.Count); // both went to the env model
    }

    [Fact]
    public async Task ModelIdFor_is_the_catalog_id_when_routed_and_the_client_name_otherwise()
    {
        var table = await TableAsync();
        var agents = new FactoryAgents(_ => new ScriptedChatClient(), new RoleAllowListPolicy(), _ledger);
        Assert.Null(agents.ModelIdFor(Roles.Implementer, ModelTier.Standard, "T-001")); // routing off: as today

        agents.UseRouting(table, _ => new ScriptedChatClient());

        foreach (var tier in new[] { ModelTier.Small, ModelTier.Standard, ModelTier.Frontier })
            Assert.Equal(table.Find("T-001", Roles.Implementer)![tier].Id, agents.ModelIdFor(Roles.Implementer, tier, "T-001"));
        Assert.Null(agents.ModelIdFor(Roles.Planner, ModelTier.Frontier)); // unrouted: client says nothing
    }
}
