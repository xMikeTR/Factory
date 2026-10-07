import json
import threading
import unittest
import urllib.error
import urllib.request

import laya_adapter
import server

CTX = {"taskId": "T-001", "turnsTaken": 3, "verifyAttempts": 1, "hasApproachNote": True,
       "lastVerifyFailed": True, "turnsWithoutChange": 0}


class SidecarTests(unittest.TestCase):
    def setUp(self):
        self.srv = server.serve(0)
        self.url = f"http://127.0.0.1:{self.srv.server_address[1]}"
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()
        self.addCleanup(self.srv.server_close)
        self.addCleanup(self.srv.shutdown)
        self.addCleanup(setattr, server.Handler, "decide", staticmethod(laya_adapter.decide))

    def post(self, body):
        req = urllib.request.Request(self.url + "/decide", data=body, method="POST",
                                     headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req) as r:
                return r.status, json.loads(r.read())
        except urllib.error.HTTPError as e:
            return e.code, json.loads(e.read())

    def use(self, decide):
        server.Handler.decide = staticmethod(decide)

    def test_without_laya_decide_is_503_so_the_heuristic_decides(self):
        status, body = self.post(json.dumps(CTX).encode())
        self.assertEqual(503, status)
        self.assertIn("laya", body["error"])

    def test_a_bound_laya_answer_is_passed_through(self):
        self.use(lambda ctx: {"kind": "plan", "tier": "frontier", "reason": f"re-plan {ctx['taskId']}", "confidence": 0.8, "extra": 1})
        status, body = self.post(json.dumps(CTX).encode())
        self.assertEqual((200, {"kind": "plan", "tier": "frontier", "reason": "re-plan T-001", "confidence": 0.8}), (status, body))

    def test_a_bad_laya_answer_is_502(self):
        self.use(lambda ctx: {"kind": "dance", "tier": "frontier", "reason": "", "confidence": 0.8})
        self.assertEqual(502, self.post(json.dumps(CTX).encode())[0])

    def test_a_malformed_context_is_400(self):
        self.assertEqual(400, self.post(b"not json")[0])
        self.assertEqual(400, self.post(json.dumps({**CTX, "turnsTaken": "3"}).encode())[0])


if __name__ == "__main__":
    unittest.main()
