using Factory.Core;

namespace Factory.Harness;

public enum TurnKind { Plan, Act, Escalate }

public sealed record ConfiguratorDecision(TurnKind Kind, ModelTier Tier, string Reason);

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
