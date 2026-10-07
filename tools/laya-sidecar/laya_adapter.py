"""The one place Laya's own API is bound. Everything else in the sidecar is the factory's contract.

decide(ctx) gets the TurnContext as a dict (taskId, turnsTaken, verifyAttempts, hasApproachNote,
lastVerifyFailed, turnsWithoutChange) and returns
    {"kind": "plan"|"act"|"escalate", "tier": "frontier"|"standard"|"small",
     "reason": str, "confidence": float in [0, 1]}
or raises LayaUnavailable, which the server answers with 503 so System 2's heuristic decides.

Laya's confidence semantics are not known yet (PLAN risk table): System 2 only trusts an answer at
or above FACTORY_LAYA_MIN_CONFIDENCE, and every decision is ledgered for calibration.
"""


class LayaUnavailable(Exception):
    pass


def decide(ctx: dict) -> dict:
    try:
        import laya  # noqa: F401  (installed on the work VM, not here)
    except ImportError as e:
        raise LayaUnavailable("laya not installed") from e
    # Bind here once Laya's routing call is known, e.g.:
    #   r = laya.route(task=ctx["taskId"], features=ctx)
    #   return {"kind": r.kind, "tier": r.tier, "reason": r.why, "confidence": r.confidence}
    raise LayaUnavailable("laya installed but its routing call is not bound in laya_adapter.decide")
