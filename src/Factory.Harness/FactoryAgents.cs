using System.ClientModel;
using Anthropic;
using System.Text.Json;
using Factory.Harness.Telemetry;
using Factory.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Factory.Harness;

/// Which wire protocol the gateway speaks. FACTORY_PROVIDER = openai (default) | anthropic.
public enum ModelProvider { Anthropic, OpenAICompatible }

/// Agents never call a provider directly: APIM -> LiteLLM (per-user key, Langfuse) -> Vertex, over
/// the OpenAI-compatible protocol; or the native Anthropic client, against the public API or a
/// gateway that fronts it. Model names are gateway aliases (or Claude model ids for Anthropic),
/// so routing changes are config, not code. A null Endpoint is the provider's public endpoint.
/// CacheControl (FACTORY_GATEWAY_CACHE_CONTROL = auto | on | off) puts Anthropic's cache breakpoints on the
/// OpenAI-compatible wire. Null (auto, the default) sends them to a model whose name says Claude, which caches only
/// where asked; other models (Gemini, OpenAI) cache the prefix themselves. The Anthropic client always sends them.
public sealed record GatewayOptions(ModelProvider Provider, Uri? Endpoint, string ApiKey, string FrontierModel, string StandardModel, string SmallModel,
    bool? CacheControl = null)
{
    /// Anthropic requires max_tokens on every request; the IChatClient default is too small for a whole file.
    public const int AnthropicMaxOutputTokens = 16_000;

    public static GatewayOptions FromEnvironment() =>
        FromEnvironment(Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? ""));

    public static GatewayOptions FromEnvironment(IReadOnlyDictionary<string, string> env)
    {
        string? Get(string name) => env.TryGetValue(name, out var v) && v.Length > 0 ? v : null;
        var provider = Get("FACTORY_PROVIDER") switch
        {
            null or "openai" => ModelProvider.OpenAICompatible,
            "anthropic" => ModelProvider.Anthropic,
            var other => throw new FactoryRejectedException("FACTORY_PROVIDER is invalid", [$"'{other}' is not openai|anthropic"]),
        };
        var anthropic = provider == ModelProvider.Anthropic;
        var key = Get("FACTORY_GATEWAY_KEY") ?? (anthropic ? Get("ANTHROPIC_API_KEY") : null)
            ?? throw new FactoryRejectedException("no gateway key",
                [anthropic ? "set FACTORY_GATEWAY_KEY or ANTHROPIC_API_KEY" : "set FACTORY_GATEWAY_KEY"]);
        var url = Get("FACTORY_GATEWAY_URL") ?? (anthropic ? null : "http://localhost:4000/v1");
        var cacheControl = Get("FACTORY_GATEWAY_CACHE_CONTROL") switch
        {
            null or "auto" => (bool?)null,
            "off" => false,
            "on" => true,
            var other => throw new FactoryRejectedException("FACTORY_GATEWAY_CACHE_CONTROL is invalid", [$"'{other}' is not auto|on|off"]),
        };
        return new(provider, url is null ? null : new Uri(url), key,
            Get("FACTORY_MODEL_FRONTIER") ?? (anthropic ? "claude-opus-5-5" : "frontier"),
            Get("FACTORY_MODEL_STANDARD") ?? (anthropic ? "claude-sonnet-5-5" : "standard"),
            Get("FACTORY_MODEL_SMALL") ?? (anthropic ? "claude-haiku-4-5" : "small"), cacheControl);
    }

    public string ModelFor(ModelTier tier) =>
        tier.IsFrontier ? FrontierModel : tier.IsStandard ? StandardModel : SmallModel;

    public IChatClient ChatClientFor(ModelTier tier) => Provider switch
    {
        ModelProvider.Anthropic => new AnthropicClient
        {
            ApiKey = ApiKey,
            BaseUrl = Endpoint?.ToString() ?? "https://api.anthropic.com",
        }.AsIChatClient(ModelFor(tier), AnthropicMaxOutputTokens).AsBuilder().Use(inner => new PromptCachingChatClient(inner)).Build(),
        ModelProvider.OpenAICompatible => new OpenAIClient(new ApiKeyCredential(ApiKey), OpenAIOptions(ModelFor(tier)))
            .GetChatClient(ModelFor(tier)).AsIChatClient().AsBuilder().Use(inner => new OpenAIPromptCacheKeyChatClient(inner)).Build(),
        _ => throw new ArgumentOutOfRangeException(nameof(Provider), Provider, null),
    };

    private OpenAIClientOptions OpenAIOptions(string model)
    {
        var o = new OpenAIClientOptions { Endpoint = Endpoint ?? new Uri("http://localhost:4000/v1") };
        if (CacheControl ?? model.Contains("claude", StringComparison.OrdinalIgnoreCase)) o.AddPolicy(new OpenAICacheControlPolicy(), System.ClientModel.Primitives.PipelinePosition.PerCall);
        return o;
    }

    // A record prints every member; the key must never reach a log, ledger row or trace.
    private bool PrintMembers(System.Text.StringBuilder b)
    {
        b.Append($"Provider = {Provider}, Endpoint = {Endpoint?.ToString() ?? "(default)"}, ApiKey = <redacted>, ");
        b.Append($"FrontierModel = {FrontierModel}, StandardModel = {StandardModel}, SmallModel = {SmallModel}, CacheControl = {CacheControl}");
        return true;
    }
}

/// Identity of one agent turn, stamped on every ledger row the middleware writes.
public sealed record AgentScope(string RunId, string Role, string? TeamId = null, string? TaskId = null);

/// The Factory.Harness entry point: every System 2 agent is built here, so every agent gets the
/// gateway client, the role's tool policy, and ledger rows for each tool call.
/// The chat client per tier is a seam: production goes through the gateway, tests script the model.
/// Every model call is a gen_ai span on FactoryTelemetry.Source, nested under the task span;
/// prompts and replies go on it only when `captureContent` is set.
public sealed class FactoryAgents(Func<string, ModelTier, IChatClient> chatFor, IToolPolicy policy, ILedger ledger, bool captureContent = false)
{
    public FactoryAgents(ModelGateways gateways, IToolPolicy policy, ILedger ledger, bool captureContent = false)
        : this(gateways.ChatClientFor, policy, ledger, captureContent) { }

    /// One model pool for every role (tests, single-provider setups).
    public FactoryAgents(Func<ModelTier, IChatClient> chatFor, IToolPolicy policy, ILedger ledger, bool captureContent = false)
        : this((_, tier) => chatFor(tier), policy, ledger, captureContent) { }

    public FactoryAgents(GatewayOptions gateway, IToolPolicy policy, ILedger ledger, bool captureContent = false)
        : this(gateway.ChatClientFor, policy, ledger, captureContent) { }

    /// The model a role's tier calls, as its client names it (null when the client does not say): turn rows carry it
    /// so designc can show which model spent what.
    public string? ModelIdFor(string role, ModelTier tier) =>
        chatFor(role, tier).GetService<ChatClientMetadata>()?.DefaultModelId;

    /// Tool results older than this many are replaced by stubs in what the model sees.
    public const int KeepRecentToolResults = 5;

    /// How many results are elided at once, so the prompt prefix holds still between elisions (prompt caching).
    /// Tool results are all kept while together under this many characters (about 20k tokens).
    public const int HoldResultChars = 80_000;
    public const int ElideBatch = 5;

    public AIAgent Create(AgentScope scope, ModelTier tier, string instructions, IList<AITool> tools, TurnBudget? budget = null, int keepRecentResults = KeepRecentToolResults)
    {
        var chat = chatFor(scope.Role, tier).AsBuilder()
            .Use(inner => new TrimmingChatClient(inner, keepRecentResults, ElideBatch, HoldResultChars))
            .Use(inner => new PromptCacheKeyChatClient(inner, PromptCacheKey.For(scope)))
            .UseOpenTelemetry(sourceName: FactoryTelemetry.SourceName, configure: o => o.EnableSensitiveData = captureContent)
            .Build();

        AIAgent agent = new ChatClientAgent(chat, instructions: instructions, name: scope.Role, tools: tools);

        // AGT governs through `policy` (AcsToolPolicy) inside this middleware, so its decisions are
        // ledgered with ours; AGT's own MAF extension (preview) is deliberately not used.
        return agent.AsBuilder()
            .Use((a, context, next, ct) => GovernedToolCallAsync(scope, tier, budget, context, next, ct))
            .Build();
    }

    /// Runs before every tool call of every agent. The governance seam: the policy decides, the
    /// ledger records the decision, and a denied call never reaches the tool.
    private async ValueTask<object?> GovernedToolCallAsync(
        AgentScope scope, ModelTier tier, TurnBudget? budget,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken ct)
    {
        var name = context.Function.Name;
        var args = context.Arguments.ToDictionary(kv => kv.Key, kv => kv.Value);
        var decision = policy.Evaluate(scope.Role, name, args);
        if (!decision.Allowed)
        {
            await LogToolCallAsync(scope, tier, name, args, "denied", decision.Reason, ct);
            return $"DENIED by policy: {decision.Reason}. This tool is not available to you; do not retry it.";
        }

        if (budget is not null && !budget.TryAdmit(name))
        {
            context.Terminate = true;
            await LogToolCallAsync(scope, tier, name, args, "capped", "turn tool-call limit reached", ct);
            return "DENIED: this turn's tool-call limit is reached. The turn is over.";
        }

        // The tool itself can still refuse (a path outside the role's writes, a protected path, a
        // command off the allow-list); that is a denial too and is ledgered as one.
        var result = await next(context, ct);
        var text = result switch { string str => str, JsonElement { ValueKind: JsonValueKind.String } j => j.GetString(), _ => null };
        if (text is not null && text.StartsWith("DENIED", StringComparison.Ordinal))
            await LogToolCallAsync(scope, tier, name, args, "denied", text, ct);
        else
            await LogToolCallAsync(scope, tier, name, args, "allowed", decision.Reason, ct);

        if (budget?.Completed(name) is { } note)
            result = text is not null ? text + note : result;
        if (budget?.Exhausted == true) context.Terminate = true;
        return result;
    }

    private ValueTask LogToolCallAsync(AgentScope scope, ModelTier tier, string tool, Dictionary<string, object?> args, string outcome, string? reason, CancellationToken ct) =>
        ledger.AppendAsync(new LedgerEvent(scope.RunId, "S2", scope.Role, LedgerKinds.ToolCall,
            TaskId: scope.TaskId, TeamId: scope.TeamId, Outcome: outcome, Model: tier.ToString(),
            Payload: JsonSerializer.Serialize(new { tool, args = Summarise(args), reason })), ct);

    // Tool arguments can be whole files; the ledger keeps the shape, not the content.
    private static Dictionary<string, string> Summarise(Dictionary<string, object?> args) =>
        args.ToDictionary(kv => kv.Key, kv =>
        {
            var s = kv.Value?.ToString() ?? "";
            return s.Length <= 200 ? s : $"<{s.Length} chars>";
        });
}
