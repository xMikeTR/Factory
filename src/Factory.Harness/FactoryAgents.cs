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
/// The chat client per tier is a seam: production goes through the gateway, tests script the model.
public sealed class FactoryAgents(Func<ModelTier, IChatClient> chatFor, IToolPolicy policy, ILedger ledger)
{
    public FactoryAgents(GatewayOptions gateway, IToolPolicy policy, ILedger ledger)
        : this(tier => GatewayChat(gateway, tier), policy, ledger) { }

    private static IChatClient GatewayChat(GatewayOptions gateway, ModelTier tier)
    {
        var client = new OpenAIClient(new ApiKeyCredential(gateway.ApiKey), new OpenAIClientOptions { Endpoint = gateway.Endpoint });
        return client.GetChatClient(gateway.ModelFor(tier)).AsIChatClient();
    }

    public AIAgent Create(AgentScope scope, ModelTier tier, string instructions, IList<AITool> tools)
    {
        var chat = chatFor(tier);

        AIAgent agent = new ChatClientAgent(chat, instructions: instructions, name: scope.Role, tools: tools);

        // VERIFY (Phase 0 spike): function-invocation middleware signature in your MAF version.
        // This is also where AGT's GovernancePolicyMiddleware goes once the ACS adapter exists:
        //   .Use(new GovernancePolicyMiddleware(acsEvaluator))
        return agent.AsBuilder()
            .Use((a, context, next, ct) => GovernedToolCallAsync(scope, tier, context, next, ct))
            .Build();
    }

    /// Runs before every tool call of every agent. The governance seam: the policy decides, the
    /// ledger records the decision, and a denied call never reaches the tool.
    /// Later: the ACS runtime replaces `policy` behind the same IToolPolicy shape.
    private async ValueTask<object?> GovernedToolCallAsync(
        AgentScope scope, ModelTier tier,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken ct)
    {
        var name = context.Function.Name;
        var args = context.Arguments.ToDictionary(kv => kv.Key, kv => kv.Value);
        var decision = policy.Evaluate(scope.Role, name, args);

        await ledger.AppendAsync(new LedgerEvent(scope.RunId, "S2", scope.Role, LedgerKinds.ToolCall,
            TaskId: scope.TaskId, TeamId: scope.TeamId,
            Outcome: decision.Allowed ? "allowed" : "denied", Model: tier.ToString(),
            Payload: JsonSerializer.Serialize(new { tool = name, args = Summarise(args), reason = decision.Reason })), ct);

        return decision.Allowed
            ? await next(context, ct)
            : $"DENIED by policy: {decision.Reason}. This tool is not available to you; do not retry it.";
    }

    // Tool arguments can be whole files; the ledger keeps the shape, not the content.
    private static Dictionary<string, string> Summarise(Dictionary<string, object?> args) =>
        args.ToDictionary(kv => kv.Key, kv =>
        {
            var s = kv.Value?.ToString() ?? "";
            return s.Length <= 200 ? s : $"<{s.Length} chars>";
        });
}
