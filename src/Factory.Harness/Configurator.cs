using System.Net.Http.Json;
using System.Text.Json;
using Factory.Core;

namespace Factory.Harness;

public enum TurnKind { Plan, Act, Escalate }

/// DecidedBy names the configurator whose decision this is (ledgered); Confidence is its own
/// self-reported score, when it gives one.
public sealed record ConfiguratorDecision(TurnKind Kind, ModelTier Tier, string Reason, string DecidedBy = "heuristic", double? Confidence = null);

/// What the configurator sees before each turn. All of it comes from the ledger and git,
/// never from the agent's own account of how it is doing.
public sealed record TurnContext(
    string TaskId,
    int TurnsTaken,
    int VerifyAttempts,
    bool HasApproachNote,
    bool LastVerifyFailed,
    int TurnsWithoutChange);

/// The System III role: routes each turn (plan, act, escalate) and picks a model tier.
/// It suggests; the task state machine still owns budgets. Jev / Laya go behind this interface.
public interface IConfigurator
{
    ValueTask<ConfiguratorDecision> DecideAsync(TurnContext ctx, CancellationToken ct = default);
}

/// Deterministic default until Jev / Laya are calibrated against ledger outcomes.
/// Every decision it makes is logged, which is the calibration data set for replacing it.
public sealed class HeuristicConfigurator(int stallTurns = 2) : IConfigurator
{
    public ValueTask<ConfiguratorDecision> DecideAsync(TurnContext ctx, CancellationToken ct = default)
    {
        ConfiguratorDecision d = ctx switch
        {
            { TurnsWithoutChange: var n } when n >= stallTurns =>
                new(TurnKind.Escalate, ModelTier.Frontier, $"{n} turns with no change to the worktree"),
            { HasApproachNote: false } =>
                new(TurnKind.Plan, ModelTier.Frontier, "no approach note yet"),
            { LastVerifyFailed: true, VerifyAttempts: >= 2 } =>
                new(TurnKind.Plan, ModelTier.Frontier, "verify failed repeatedly; re-plan"),
            { LastVerifyFailed: true } =>
                new(TurnKind.Act, ModelTier.Frontier, "verify failed; stronger model for the fix"),
            _ => new(TurnKind.Act, ModelTier.Standard, "routine implementation turn"),
        };
        return ValueTask.FromResult(d);
    }
}

/// Client for tools/laya-sidecar: POST /decide with the TurnContext, answer
/// {kind: plan|act|escalate, tier: frontier|standard|small, reason, confidence}.
/// Throws when the sidecar is down, answers non-2xx, or answers something it cannot read;
/// FallbackConfigurator turns that into a heuristic decision.
public sealed class LayaConfigurator(HttpClient http) : IConfigurator
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Answer(string? Kind, string? Tier, string? Reason, double? Confidence);

    public async ValueTask<ConfiguratorDecision> DecideAsync(TurnContext ctx, CancellationToken ct = default)
    {
        // A sized body, not chunked: simple sidecars (http.server) read by Content-Length.
        using var body = new StringContent(JsonSerializer.Serialize(ctx, Json), System.Text.Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("decide", body, ct);
        response.EnsureSuccessStatusCode();
        var a = await response.Content.ReadFromJsonAsync<Answer>(Json, ct)
            ?? throw new InvalidDataException("empty answer");
        TurnKind kind = a.Kind switch
        {
            "plan" => TurnKind.Plan,
            "act" => TurnKind.Act,
            "escalate" => TurnKind.Escalate,
            var k => throw new InvalidDataException($"kind '{k}' is not plan|act|escalate"),
        };
        ModelTier tier = a.Tier switch
        {
            "frontier" => ModelTier.Frontier,
            "standard" => ModelTier.Standard,
            "small" => ModelTier.Small,
            var t => throw new InvalidDataException($"tier '{t}' is not frontier|standard|small"),
        };
        return new(kind, tier, a.Reason ?? "", "laya", a.Confidence);
    }
}

/// Primary decides when it answers with confidence >= minConfidence; otherwise the fallback
/// decides and its reason says why the primary was passed over. A primary that gives no
/// confidence counts as unconfident. Cancellation is never treated as the primary being down.
public sealed class FallbackConfigurator(IConfigurator primary, IConfigurator fallback, double minConfidence) : IConfigurator
{
    public async ValueTask<ConfiguratorDecision> DecideAsync(TurnContext ctx, CancellationToken ct = default)
    {
        string why;
        try
        {
            var d = await primary.DecideAsync(ctx, ct);
            if (d.Confidence >= minConfidence) return d;
            why = $"{d.DecidedBy} confidence {d.Confidence?.ToString() ?? "none"} < {minConfidence}";
        }
        catch (Exception e) when (e is HttpRequestException or InvalidDataException or JsonException
                                    or NotSupportedException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            why = $"laya unavailable ({e.GetType().Name}: {e.Message})";
        }
        var f = await fallback.DecideAsync(ctx, ct);
        return f with { Reason = $"{f.Reason} [{why}]" };
    }
}
