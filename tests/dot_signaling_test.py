"""Exercise the native protocol without network access, login or a real dot call."""
import base64
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("dot_signaling", Path(__file__).parents[1] / "src/Joydex.Windows/Voice/dot-signaling.py")
SIGNALING = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SIGNALING)
TARGET = {"accountId": "account-test", "userId": "user-test", "dotId": "account-test~dot-test",
          "threadId": "01000000-0000-0000-0000-000000000000"}


class Response(io.BytesIO):
    """A bounded HTTP response with no external effects."""

    def __init__(self, status, body=b"", location=None):
        super().__init__(body)
        self.status = status
        self.headers = {"Location": location} if location else {}


class DotSignalingTests(unittest.TestCase):
    def exercise(self, commands, responses):
        """Feed commands through the actual worker with owned fake auth and HTTP."""
        identity = {"chatgpt_account_id": TARGET["accountId"], "chatgpt_user_id": TARGET["userId"]}
        claims = base64.urlsafe_b64encode(json.dumps({"https://api.openai.com/auth": identity}).encode()).decode()
        auth = [{"id": 1, "result": {}}, {"id": 2, "result": {"authMethod": "chatgpt", "authToken": "fake." + claims + ".fake"}},
                {"id": 3, "result": {"workspaceRouting": {"chatgptAccountId": TARGET["accountId"],
                 "backendOrigin": "https://chatgpt.com", "accountRoutingOverride": "NO_CONSTRAINT"}}}]
        class Process:
            stdin = io.StringIO()
            stdout = io.StringIO("".join(json.dumps(value) + "\n" for value in auth))
            def wait(self, timeout):
                return 0
        requests = []
        class Opener:
            def open(self, request, timeout):
                requests.append(request)
                response = responses.pop(0)
                if isinstance(response, Exception):
                    raise response
                return response
        output = io.StringIO()
        with tempfile.TemporaryDirectory() as directory:
            binary = Path(directory) / "codex.exe"
            binary.write_bytes(b"")
            # The production hash gate still executes, using this test-only expected fixture hash.
            with patch.object(SIGNALING, "CODEX_SHA256", hashlib.sha256(b"").hexdigest().upper()), \
                 patch.object(SIGNALING.subprocess, "Popen", return_value=Process()), \
                 patch.object(SIGNALING.urllib.request, "build_opener", return_value=Opener()), \
                 patch.object(SIGNALING.sys, "stdin", io.StringIO("".join(json.dumps({"id": index, **command}) + "\n" for index, command in enumerate(commands, 1)))), \
                 patch.object(SIGNALING.sys, "stdout", output):
                SIGNALING.run(str(binary))
        return [json.loads(line) for line in output.getvalue().splitlines()], requests

    @staticmethod
    def profile():
        return Response(200, json.dumps({"id": TARGET["dotId"], "display_name": "Example dot"}).encode())

    def test_saved_thread_is_checked_and_call_is_created_attached_stopped_once(self):
        results, requests = self.exercise([
            {"op": "verify", **TARGET}, {"op": "create", "sdp": "v=0\r\n"}, {"op": "attach"}, {"op": "stop"}],
            [self.profile(), Response(201, b"v=0\r\n", "rtc_test"), Response(200), Response(200)])
        self.assertTrue(results[-1]["result"]["stopped"])
        self.assertIn("tbo/by-thread/" + TARGET["threadId"], requests[0].full_url)
        self.assertEqual(["GET", "POST", "POST", "POST"], [request.method for request in requests])
        self.assertNotIn("authToken", json.dumps(results))
        self.assertNotIn("fake.fake", json.dumps(results))

    def test_account_switch_prevents_even_a_profile_lookup(self):
        results, requests = self.exercise([{ "op": "verify", **TARGET, "userId": "another-user"}], [])
        self.assertEqual("AccountIdentityChanged", results[0]["failure"])
        self.assertEqual([], requests)

    def test_changed_dot_cannot_allocate(self):
        results, requests = self.exercise([{ "op": "verify", **TARGET}, {"op": "create", "sdp": "v=0"}],
            [Response(200, b'{"id":"different-dot","display_name":"Other"}')])
        self.assertEqual("DotTargetChanged", results[0]["failure"])
        self.assertEqual("CallNotReady", results[1]["failure"])
        self.assertEqual(1, len(requests))

    def test_ambiguous_create_cannot_allocate_again_or_claim_a_confirmed_stop(self):
        results, requests = self.exercise([{ "op": "verify", **TARGET}, {"op": "create", "sdp": "v=0"},
            {"op": "create", "sdp": "v=0"}, {"op": "stop"}], [self.profile(), TimeoutError()])
        self.assertEqual("CallNotReady", results[2]["failure"])
        self.assertEqual("AllocationUncertain", results[3]["failure"])
        self.assertEqual(2, len(requests))

    def test_known_allocation_is_cleaned_up_after_a_bad_create_response(self):
        results, requests = self.exercise([{ "op": "verify", **TARGET}, {"op": "create", "sdp": "v=0"}, {"op": "stop"}],
            [self.profile(), Response(500, b"private failure", "rtc_test"), Response(200)])
        self.assertEqual("DotCallRejected", results[1]["failure"])
        self.assertTrue(results[2]["result"]["stopped"])
        self.assertEqual(3, len(requests))

    def test_rejected_stop_is_not_retried_on_pipe_close(self):
        results, requests = self.exercise([{ "op": "verify", **TARGET}, {"op": "create", "sdp": "v=0"}, {"op": "stop"}],
            [self.profile(), Response(201, b"v=0", "rtc_test"), Response(500)])
        self.assertEqual("DotCallStopRejected", results[2]["failure"])
        self.assertEqual(3, len(requests))


if __name__ == "__main__":
    unittest.main()
