"""The one place a routing model is bound. Everything else in the sidecar is the factory's contract.

decide(ctx) gets the TurnContext as a dict (taskId, turnsTaken, verifyAttempts, hasApproachNote,
lastVerifyFailed, turnsWithoutChange) and returns
    {"kind": "plan"|"act"|"escalate", "tier": "frontier"|"standard"|"small",
     "reason": str, "confidence": float in [0, 1]}
or raises RouterUnavailable, which the server answers with 503 so System 2's heuristic decides.

No router is bound yet: until one is (an approved model, e.g. a small Vertex model), every call
raises and the heuristic decides. System 2 only trusts an answer at or above
FACTORY_ROUTER_MIN_CONFIDENCE, and every decision is ledgered for calibration.
"""


class RouterUnavailable(Exception):
    pass


def decide(ctx: dict) -> dict:
    # Bind here once a router is chosen, e.g.:
    #   r = my_router.route(features=ctx)
    #   return {"kind": r.kind, "tier": r.tier, "reason": r.why, "confidence": r.confidence}
    raise RouterUnavailable("no router bound in router_adapter.decide")
