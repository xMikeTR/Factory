"""Laya sidecar for System 2's LayaConfigurator.

    python3 tools/laya-sidecar/server.py [--port 8765]    then FACTORY_LAYA_URL=http://127.0.0.1:8765/

GET  /health  -> 200 {"laya": "available"|"unavailable", "detail": ...}
POST /decide  TurnContext JSON -> 200 decision | 400 bad request | 503 Laya unavailable | 502 bad Laya answer
Binds to loopback only. No secrets pass through it: the TurnContext is counters and a task id.
"""
import argparse
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import laya_adapter

KINDS = {"plan", "act", "escalate"}
TIERS = {"frontier", "standard", "small"}
FIELDS = {"taskId": str, "turnsTaken": int, "verifyAttempts": int, "hasApproachNote": bool,
          "lastVerifyFailed": bool, "turnsWithoutChange": int}


def check_context(ctx):
    if not isinstance(ctx, dict):
        return "body must be a JSON object"
    for name, typ in FIELDS.items():
        v = ctx.get(name)
        if not isinstance(v, typ) or (typ is int and isinstance(v, bool)):
            return f"{name}: required {typ.__name__}"
    return None


def check_decision(d):
    if not isinstance(d, dict):
        return "decision must be an object"
    if d.get("kind") not in KINDS:
        return f"kind {d.get('kind')!r} is not plan|act|escalate"
    if d.get("tier") not in TIERS:
        return f"tier {d.get('tier')!r} is not frontier|standard|small"
    c = d.get("confidence")
    if not isinstance(c, (int, float)) or isinstance(c, bool) or not 0 <= c <= 1:
        return "confidence must be a number in [0, 1]"
    if not isinstance(d.get("reason"), str):
        return "reason must be a string"
    return None


class Handler(BaseHTTPRequestHandler):
    decide = staticmethod(laya_adapter.decide)

    def _send(self, status, body):
        data = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if self.path != "/health":
            return self._send(404, {"error": "not found"})
        try:
            self.decide({"taskId": "health", "turnsTaken": 0, "verifyAttempts": 0, "hasApproachNote": False,
                         "lastVerifyFailed": False, "turnsWithoutChange": 0})
            self._send(200, {"laya": "available"})
        except laya_adapter.LayaUnavailable as e:
            self._send(200, {"laya": "unavailable", "detail": str(e)})

    def do_POST(self):
        if self.path != "/decide":
            return self._send(404, {"error": "not found"})
        try:
            length = int(self.headers.get("Content-Length") or 0)
            ctx = json.loads(self.rfile.read(length) or b"null")
        except (ValueError, json.JSONDecodeError):
            return self._send(400, {"error": "body is not JSON"})
        if err := check_context(ctx):
            return self._send(400, {"error": err})
        try:
            d = self.decide(ctx)
        except laya_adapter.LayaUnavailable as e:
            return self._send(503, {"error": str(e)})
        if err := check_decision(d):
            return self._send(502, {"error": f"laya answered badly: {err}"})
        self._send(200, {k: d[k] for k in ("kind", "tier", "reason", "confidence")})

    def log_message(self, fmt, *args):  # keep stdout quiet; System 2 ledgers every decision
        pass


def serve(port):
    return ThreadingHTTPServer(("127.0.0.1", port), Handler)


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("--port", type=int, default=8765)
    server = serve(p.parse_args().port)
    print(f"laya-sidecar on http://127.0.0.1:{server.server_address[1]}/", flush=True)
    server.serve_forever()
