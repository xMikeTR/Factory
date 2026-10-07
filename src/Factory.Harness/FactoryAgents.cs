using System.ClientModel;
using System.Text.Json;
using Factory.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Factory.Harness;

/// Agents never call a provider directly: APIM -> LiteLLM (per-user key, Langfuse) -> Vertex.
/// Model names are LiteLLM aliases, so routing changes are gateway config, not code.
public sealed record GatewayOptions(Uri Endpoint, string ApiKey, string FrontierModel, string StandardModel, string SmallModel)
{
    public static GatewayOptions FromEnvironment() => new(
        new Uri(Env("FACTORY_GATEWAY_URL", "http://localhost:4000/v1")),
        Env("FACTORY_GATEWAY_KEY"),
        Env("FACTORY_MODEL_FRONTIER", "frontier"),
        Env("FACTORY_MODEL_STANDARD", "standard"),
        Env("FACTORY_MODEL_SMALL", "small"));

    public string ModelFor(ModelTier tier) =>
        tier.IsFrontier ? FrontierModel : tier.IsStandard ? StandardModel : SmallModel;

    private static string Env(string name, string? fallback = null) =>
        Environment.GetEnvironmentVariable(name) ?? fallback
        ?? throw new InvalidOperationException($"environment variable {name} is required");
}

/// Identity of one agent turn, stamped on every ledger row the middleware writes.
public sealed record AgentScope(string RunId, string Role, string? TeamId = null, string? TaskId = null);

/// The Factory.Harness entry point: every System 2 agent is built here, so every agent gets the
/// gateway client, the role's tool policy, and ledger rows for each tool call.
public sealed class FactoryAgents(GatewayOptions gateway, IToolPolicy policy, ILedger ledger)
{
    public AIAgent Create(AgentScope scope, ModelTier tier, string instructions, IList<AITool> tools)
    {
        var client = new OpenAIClient(new ApiKeyCredential(gateway.ApiKey), new OpenAIClientOptions { Endpoint = gateway.Endpoint });
        IChatClient chat = client.GetChatClient(gateway.ModelFor(tier)).AsIChatClient();

        AIAgent agent = new ChatClientAgent(chat, instructions: instructions, name: scope.Role, tools: tools);

        // VERIFY (Phase 0 spike): function-invocation middleware signature in your MAF version.
        // This is also where AGT's GovernancePolicyMiddleware goes once the ACS adapter exists:
        //   .Use(new GovernancePolicyMiddleware(acsEvaluator))
        return agent.AsBuilder()
            .Use((a, context, next, ct) => GovernedToolCallAsync(scope, tier, context, next, ct))
            .Build();
    }

    /// Runs before every tool call of every agent. The governance seam: yours to own.
    ///
    /// TODO(you) — RESOURCES.md §9.
    ///   1. name = context.Function.Name; args = context.Arguments as a dictionary
    ///   2. decision = policy.Evaluate(scope.Role, name, args)       (later: the ACS runtime)
    ///   3. ledger row: kind tool_call, outcome allowed|denied, model, payload {tool, Summarise(args), reason}
    ///   4. denied -> return a string the model can act on ("DENIED by policy: ... do not retry")
    ///      and do NOT call next; allowed -> return await next(context, ct)
    private ValueTask<object?> GovernedToolCallAsync(
        AgentScope scope, ModelTier tier,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken ct) =>
        throw new NotImplementedException("TODO: FactoryAgents.GovernedToolCallAsync (RESOURCES.md §9)");

    // Tool arguments can be whole files; the ledger keeps the shape, not the content.
    private static Dictionary<string, string> Summarise(Dictionary<string, object?> args) =>
        args.ToDictionary(kv => kv.Key, kv =>
        {
            var s = kv.Value?.ToString() ?? "";
            return s.Length <= 200 ? s : $"<{s.Length} chars>";
        });
}
