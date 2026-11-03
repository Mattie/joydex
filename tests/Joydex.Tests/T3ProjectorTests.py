"""Offline fixtures: no SSH, grant issuance, listener, desktop UI or live files."""
import copy
import contextlib
import os
import base64
import hashlib
import importlib.util
import json
import pathlib
import tempfile
import struct
import unittest
from unittest.mock import patch

SCRIPT = pathlib.Path(__file__).resolve().parents[2] / "src/Joydex.Windows/T3/t3-projector.py"
SPEC = importlib.util.spec_from_file_location("t3_projector", SCRIPT)
PROJECTOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PROJECTOR)
STAMP = "2026-10-20T10:20:30Z"


class Response:
    def __init__(self, data):
        self.raw = data if isinstance(data, bytes) else json.dumps(data).encode()
    def __enter__(self):
        return self
    def __exit__(self, *_):
        pass
    def read(self, size):
        return self.raw[:size]


class T3ProjectorTests(unittest.TestCase):
    def test_catalog_boundary_is_captured_before_shell_not_after_slow_details(self):
        real_datetime = PROJECTOR.datetime.datetime
        before_shell = real_datetime(2026, 10, 20, 10, 20, 30, tzinfo=PROJECTOR.datetime.timezone.utc)
        clock = {"now": before_shell}
        class Clock(real_datetime):
            @classmethod
            def now(cls, tz=None):
                return clock["now"]
        def response(request, timeout):
            clock["now"] = before_shell + PROJECTOR.datetime.timedelta(seconds=20 if "/threads/" in request.full_url else 10)
            return Response(self.detail if "/threads/" in request.full_url else self.shell)
        with patch.object(PROJECTOR.datetime, "datetime", Clock), patch.object(self, "open", side_effect=response):
            result = self.read(knownThreadIds=["thread-1"])
        self.assertEqual(result["catalogObservedAt"], before_shell.isoformat())
        self.assertEqual(result["capturedAt"], (before_shell + PROJECTOR.datetime.timedelta(seconds=20)).isoformat())
        self.assertIn("revoke", self.calls[-1])

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = pathlib.Path(self.directory.name)
        if os.name == "nt":
            lock_patch = patch.object(PROJECTOR, "shortcut_mutation_lock", lambda _: contextlib.nullcontext())
            lock_patch.start()
            self.addCleanup(lock_patch.stop)
        (self.root / "environment-id").write_text("env-one")
        (self.root / "server-runtime.json").write_text(json.dumps({"origin": "http://127.0.0.1:12345", "pid": 7, "startedAt": STAMP}))
        self.calls, self.paths = [], []
        self.shell = {"snapshotSequence": 3, "projects": [{"id": "project-1", "title": "Project"}], "threads": [{
            "id": "thread-1", "projectId": "project-1", "title": "Task", "status": "running", "activeRunId": "run-new",
            "latestRunStartedAt": STAMP, "latestUserAuthoredMessageAt": STAMP,
            "latestVisibleMessage": {"text": "PRIVATE MESSAGE BODY"}, "pendingRuntimeRequest": {"id": "one-summary-only"}}]}
        self.detail = {"snapshotSequence": 4, "projection": {"thread": {"id": "thread-1", "title": "Task"},
            "nodes": [{"id": "old-node", "runId": "run-old"}, {"id": "new-node", "runId": "run-new"}],
            "runs": [{"id": "run-new", "ordinal": 2, "status": "completed", "startedAt": STAMP, "completedAt": STAMP}],
            "messages": [{"text": "PRIVATE MESSAGE BODY"}], "runtimeRequests": [
                self.request("question", "old-node", "message"), self.request("approval", "new-node", "live"),
                self.request("answered", "old-node", "message", "resolved"), self.request("expired", "old-node", "message", "expired")]}}
        self.cli_patch = patch.object(PROJECTOR, "cli", side_effect=self.fake_cli)
        self.cli_patch.start()
        self.addCleanup(self.cli_patch.stop)
        opener_patch = patch.object(PROJECTOR.urllib.request, "build_opener", return_value=self)
        opener_patch.start()
        self.addCleanup(opener_patch.stop)

    @staticmethod
    def request(request_id, node_id, capability, status="pending"):
        return {"id": request_id, "nodeId": node_id, "status": status, "createdAt": STAMP,
                "responseCapability": {"type": capability}, "answers": "PRIVATE ANSWER"}

    def fake_cli(self, executable, arguments, timeout):
        self.calls.append(arguments)
        if "issue" in arguments:
            return json.dumps({"token": "PRIVATE TOKEN", "sessionId": "short-session", "scopes": ["orchestration:read"]}).encode()
        return b"{}"

    def open(self, request, timeout):
        self.paths.append(request.full_url)
        return Response(self.detail if "/threads/" in request.full_url else self.shell)

    def read(self, **kwargs):
        return PROJECTOR.read_metadata(self.root, pathlib.Path("fake-t3"), {"mode": "read", "environmentId": "env-one", **kwargs})

    @staticmethod
    def resolved(command, key, condition=None):
        tokens = key.split("+")
        rule = {"command": command, "shortcut": {"key": tokens[-1], "metaKey": "meta" in tokens,
                "ctrlKey": "ctrl" in tokens, "shiftKey": "shift" in tokens, "altKey": "alt" in tokens, "modKey": "mod" in tokens}}
        if condition:
            rule["whenAst"] = ({"type": "not", "node": {"type": "identifier", "name": condition[1:]}}
                               if condition.startswith("!") else {"type": "identifier", "name": condition})
        return rule

    def effective_config(self):
        return {"keybindingsConfigPath": str(self.root / "keybindings.json"), "issues": [], "keybindings": [
            self.resolved("terminal.new", "mod+n", "terminalFocus"), self.resolved("chat.new", "mod+n", "!terminalFocus"),
            self.resolved("chat.new", "mod+shift+o", "!terminalFocus"), self.resolved("thread.previous", "mod+shift+["),
            self.resolved("thread.next", "mod+shift+]")]}

    def setup(self, remove=False, config=None):
        return PROJECTOR.setup_shortcuts(self.root, remove, config if config is not None else self.effective_config())

    def test_full_requests_keep_run_identity_and_only_explicit_answered_questions(self):
        result = self.read(knownThreadIds=["thread-1"])
        task = result["tasks"][0]
        self.assertEqual(result["sequence"], 4)
        self.assertTrue(task["requestsComplete"])
        self.assertEqual([(r["id"], r["runId"], r["blocking"]) for r in task["pendingRequests"]],
                         [("question", "run-old", False), ("approval", "run-new", True)])
        self.assertEqual([r["id"] for r in task["resolvedQuestions"]], ["answered"])
        self.assertEqual(task["latestUserAuthoredMessageAt"], "2026-10-20T10:20:30+00:00")
        self.assertNotIn("PRIVATE", json.dumps(result))
        self.assertIn("revoke", self.calls[-1])
        self.assertIn("5m", self.calls[0])
        self.assertEqual(self.calls[0].count("orchestration:read"), 1)

    def test_missing_human_timestamp_does_not_fall_back_to_generic_update(self):
        self.shell["threads"][0].pop("latestUserAuthoredMessageAt")
        self.shell["threads"][0]["updatedAt"] = STAMP
        self.shell["threads"][0]["latestUserMessageAt"] = STAMP
        self.assertIsNone(self.read()["tasks"][0]["latestUserAuthoredMessageAt"])

    def test_optional_binding_read_failure_does_not_disable_metadata_monitoring(self):
        with patch.object(PROJECTOR, "fingerprint", side_effect=PermissionError("PRIVATE PATH")):
            result = self.read()
        self.assertTrue(result["success"])
        self.assertIsNone(result["bindingFingerprint"])
        self.assertIn("revoke", self.calls[-1])

    def test_detail_failure_is_partial_and_cannot_complete_or_clear_requests(self):
        self.detail = {"unsupported": True}
        result = self.read(knownThreadIds=["thread-1"])
        self.assertFalse(result["isComplete"])
        self.assertFalse(result["tasks"][0]["readComplete"])
        self.assertFalse(result["tasks"][0]["requestsComplete"])
        self.assertIn("revoke", self.calls[-1])

    def test_tracked_thread_absent_from_shell_is_read_directly(self):
        self.shell["threads"] = []
        task = self.read(knownThreadIds=["thread-1"])["tasks"][0]
        self.assertEqual(task["status"], "completed")
        self.assertEqual(task["runId"], "run-new")
        self.assertTrue(task["readComplete"])
        self.assertTrue(any("/threads/thread-1" in path for path in self.paths))

    def test_full_detail_recovers_optional_shell_timestamps_for_the_exact_selected_run(self):
        thread = self.shell["threads"][0]
        thread.pop("latestRunStartedAt")
        thread["updatedAt"] = "2026-10-21T10:20:30Z"
        self.detail["projection"]["runs"].append({"id": "different-run", "ordinal": 99, "status": "running",
                                                  "startedAt": "2026-10-22T10:20:30Z"})
        task = self.read(knownThreadIds=["thread-1"])["tasks"][0]
        self.assertEqual(task["runId"], "run-new")
        self.assertEqual(task["startedAt"], "2026-10-20T10:20:30+00:00")
        self.assertEqual(task["completedAt"], "2026-10-20T10:20:30+00:00")
        self.assertEqual(task["status"], "completed")

    def test_new_active_shell_missing_optional_lifecycle_fields_is_read_before_tracking(self):
        thread = self.shell["threads"][0]
        thread.pop("latestRunStartedAt")
        thread.pop("activeRunId")
        thread.pop("pendingRuntimeRequest")
        self.detail["projection"]["runs"][0].update(status="queued", startedAt=None, requestedAt=STAMP, completedAt=None)
        task = self.read()["tasks"][0]
        self.assertEqual(task["runId"], "run-new")
        self.assertEqual(task["startedAt"], "2026-10-20T10:20:30+00:00")
        self.assertTrue(task["readComplete"])
        self.assertTrue(any("/threads/thread-1" in path for path in self.paths))

    def test_ambiguous_active_detail_does_not_guess_run_identity(self):
        thread = self.shell["threads"][0]
        thread.pop("activeRunId")
        thread.pop("pendingRuntimeRequest")
        self.detail["projection"]["runs"] = [{"id": "one", "status": "running", "startedAt": STAMP},
                                                 {"id": "two", "status": "queued", "requestedAt": STAMP}]
        task = self.read()["tasks"][0]
        self.assertIsNone(task["runId"])
        self.assertFalse(task["readComplete"])

    def test_unknown_request_join_and_bounded_history_prevent_authoritative_clear(self):
        self.detail["hasMoreHistory"] = True
        self.detail["projection"]["runtimeRequests"].append(self.request("unjoined", "missing", "live"))
        self.assertFalse(self.read()["tasks"][0]["requestsComplete"])

    def test_wrong_pin_is_rejected_before_issuing_credential(self):
        with self.assertRaises(PROJECTOR.SafeError) as error:
            self.read(environmentId="different")
        self.assertEqual(error.exception.kind, "EnvironmentMismatch")
        self.assertEqual(self.calls, [])

    def test_non_loopback_origin_is_rejected_before_issuing_credential(self):
        for origin in ("https://example.com", "http://127.0.0.1@evil.test", "http://127.0.0.1/path"):
            (self.root / "server-runtime.json").write_text(json.dumps({"origin": origin, "startedAt": STAMP}))
            with self.assertRaises(PROJECTOR.SafeError):
                self.read()
        self.assertEqual(self.calls, [])

    def test_redirect_handler_rejects_redirect(self):
        with self.assertRaises(PROJECTOR.SafeError):
            PROJECTOR.NoRedirect().redirect_request(None, None, None, None, None, None)

    def test_wrong_scopes_are_revoked_without_http(self):
        original = self.fake_cli
        def changed_cli(executable, arguments, timeout):
            result = original(executable, arguments, timeout)
            if "issue" in arguments:
                result = json.dumps({"token": "PRIVATE", "sessionId": "session", "scopes": ["orchestration:write"]}).encode()
            return result
        with patch.object(PROJECTOR, "cli", side_effect=changed_cli), self.assertRaises(PROJECTOR.SafeError) as error:
            self.read()
        self.assertEqual(error.exception.kind, "UnexpectedScopes")
        self.assertIn("revoke", self.calls[-1])
        self.assertEqual(self.paths, [])

    def test_revoke_failure_withholds_metadata_and_reports_finite_fallback(self):
        def failed_revoke(executable, arguments, timeout):
            if "revoke" in arguments:
                raise RuntimeError("PRIVATE TOKEN")
            return self.fake_cli(executable, arguments, timeout)
        with patch.object(PROJECTOR, "cli", side_effect=failed_revoke), self.assertRaises(PROJECTOR.SafeError) as error:
            self.read()
        self.assertEqual(error.exception.kind, "RevokeFailed")
        self.assertIn("five minutes", error.exception.reason)
        self.assertNotIn("PRIVATE", error.exception.reason)

    def test_oversized_shell_is_rejected_and_revoked(self):
        with patch.object(self, "open", return_value=Response(b"x" * (PROJECTOR.MAX_BYTES + 1))), self.assertRaises(PROJECTOR.SafeError) as error:
            self.read()
        self.assertEqual(error.exception.kind, "PayloadLimit")
        self.assertIn("revoke", self.calls[-1])

    def test_restart_epoch_changes_and_mid_read_restart_is_rejected(self):
        first = self.read()["sourceEpoch"]
        runtime = self.root / "server-runtime.json"
        runtime.write_text(json.dumps({"origin": "http://127.0.0.1:12345", "pid": 8, "startedAt": STAMP}))
        self.assertNotEqual(first, self.read()["sourceEpoch"])
        original = self.open
        def restart(request, timeout):
            runtime.write_text(json.dumps({"origin": "http://127.0.0.1:12345", "pid": 9, "startedAt": STAMP}))
            return original(request, timeout)
        with patch.object(self, "open", side_effect=restart), self.assertRaises(PROJECTOR.SafeError) as error:
            self.read()
        self.assertEqual(error.exception.kind, "RuntimeChanged")

    def test_setup_preserves_rule_order_and_undo_removes_only_exact_owned_rules(self):
        path = self.root / "keybindings.json"
        original = [{"key": "alt+n", "command": "chat.new", "when": "isDesktop", "extension": "PRIVATE EXTRA FIELD"},
                    {"key": "shift+mod+alt+1", "command": "custom.command"}]
        path.write_text(json.dumps(original))
        result = self.setup()
        rules = json.loads(path.read_text())
        self.assertEqual([r for r in rules if r in original], original)
        self.assertEqual(result["addedCommands"], ["thread.previous", "thread.next", "thread.stop"])
        self.assertNotIn("PRIVATE", json.dumps(result))
        dedicated = [r for r in rules if r.get("when") == "isDesktop" and r not in original]
        self.assertEqual(dedicated[0]["key"], "ctrl+alt+shift+2")
        fingerprint = result["bindingFingerprint"]
        repeated = self.setup()
        self.assertEqual(repeated["addedCommands"], [])
        self.assertEqual(repeated["bindingFingerprint"], fingerprint)
        edited = next(r for r in rules if r.get("key") == "ctrl+alt+shift+2")
        edited["key"] = "alt+x"  # A later human edit must survive removal.
        path.write_text(json.dumps(rules))
        self.setup(True)
        self.assertEqual(json.loads(path.read_text()), original + [edited])
        self.assertTrue(list(self.root.glob(".joydex-keybindings-backup-*.json")))

    def test_duplicate_added_rule_is_retained_and_setup_is_not_called_by_monitor(self):
        self.setup()
        path = self.root / "keybindings.json"
        rules = json.loads(path.read_text())
        rules.append(copy.deepcopy(rules[0]))
        path.write_text(json.dumps(rules))
        self.setup(True)
        self.assertEqual(len(json.loads(path.read_text())), 2)
        before = path.read_bytes()
        self.read()
        self.assertEqual(path.read_bytes(), before)

    def test_setup_retains_all_effective_default_chords_and_journals_every_addition(self):
        result = self.setup()
        rules = json.loads((self.root / "keybindings.json").read_text())
        self.assertEqual(len(rules), 8)  # Four effective defaults plus four dedicated chords.
        self.assertIn({"key": "mod+n", "command": "chat.new", "when": "!(terminalFocus)"}, rules)
        self.assertIn({"key": "shift+mod+o", "command": "chat.new", "when": "!(terminalFocus)"}, rules)
        self.assertIn({"key": "shift+mod+[", "command": "thread.previous"}, rules)
        self.assertIn({"key": "shift+mod+]", "command": "thread.next"}, rules)
        manifest = json.loads((self.root / ".joydex-keybinding-additions.json").read_text())
        self.assertEqual(len(manifest["rules"]), 8)
        self.assertEqual(result["rules"], rules)
        self.assertIn("server.getConfig", result["capabilitySource"])
        self.setup(True)
        self.assertEqual(json.loads((self.root / "keybindings.json").read_text()), [])

    def test_unknown_effective_configuration_causes_preview_without_writes(self):
        result = PROJECTOR.setup_shortcuts(self.root, False)
        self.assertFalse(result["success"])
        self.assertEqual(result["errorClass"], "UnsupportedShortcuts")
        self.assertFalse((self.root / "keybindings.json").exists())
        self.assertFalse((self.root / ".joydex-keybinding-additions.json").exists())
        self.assertEqual(self.calls, [])

    def test_preserving_defaults_must_not_change_last_matching_command(self):
        config = self.effective_config()
        # A future server catalog may give a later default the same chord/context.
        # Moving chat.new's default into the custom prefix would then change its winner.
        config["keybindings"].append(self.resolved("another.command", "ctrl+n", "!terminalFocus"))
        with self.assertRaises(PROJECTOR.SafeError) as error:
            self.setup(config=config)
        self.assertEqual(error.exception.kind, "BindingConflict")
        self.assertFalse((self.root / "keybindings.json").exists())

    def test_server_configuration_read_uses_scoped_grant_and_revokes_before_return(self):
        config = self.effective_config()
        with patch.object(self, "open", return_value=Response({"ticket": "private-ticket"})), \
                patch.object(PROJECTOR, "websocket_server_config", return_value=config) as websocket:
            result = PROJECTOR.read_effective_bindings(self.root, pathlib.Path("fake-t3"))
        self.assertEqual(result, config)
        self.assertIn("revoke", self.calls[-1])
        self.assertEqual(websocket.call_args.args, ("http://127.0.0.1:12345", "private-ticket"))
        self.assertIn("orchestration:read", self.calls[0])

    def test_server_configuration_cannot_redirect_setup_to_another_binding_file(self):
        config = self.effective_config()
        config["keybindingsConfigPath"] = str(self.root / "another.json")
        with patch.object(self, "open", return_value=Response({"ticket": "private-ticket"})), \
                patch.object(PROJECTOR, "websocket_server_config", return_value=config), self.assertRaises(ValueError):
            PROJECTOR.read_effective_bindings(self.root, pathlib.Path("fake-t3"))
        self.assertIn("revoke", self.calls[-1])


class FakeSocket:
    def __init__(self, envelopes=None, fragmented=False, invalid_handshake=False, payload=None):
        self.pending = bytearray()
        self.sent = []
        self.closed = False
        self.envelopes = envelopes
        self.fragmented = fragmented
        self.invalid_handshake = invalid_handshake
        self.payload = payload

    def getpeername(self):
        return ("127.0.0.1", 12345)

    def settimeout(self, _):
        pass

    def sendall(self, raw):
        self.sent.append(raw)
        if raw.startswith(b"GET "):
            key = raw.split(b"Sec-WebSocket-Key: ")[1].split(b"\r\n")[0]
            accept = base64.b64encode(hashlib.sha1(key + b"258EAFA5-E914-47DA-95CA-C5AB0DC85B11").digest())
            if self.invalid_handshake:
                accept = b"wrong"
            self.pending.extend(b"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + b"\r\n\r\n")
        elif raw[0] & 15 == 1:
            payload = self.payload or json.dumps(self.envelopes).encode()
            if self.fragmented:
                cut = len(payload) // 2
                self.pending.extend(self.frame(1, payload[:cut], final=False) + self.frame(9, b"ping") + self.frame(0, payload[cut:]))
            else:
                self.pending.extend(self.frame(1, payload))

    @staticmethod
    def frame(opcode, payload, final=True):
        prefix = bytes([(0x80 if final else 0) | opcode])
        length = len(payload)
        if length < 126:
            return prefix + bytes([length]) + payload
        if length < 65536:
            return prefix + bytes([126]) + struct.pack("!H", length) + payload
        return prefix + bytes([127]) + struct.pack("!Q", length) + payload

    def recv(self, size):
        # Deliberately split reads to test transport framing boundaries.
        size = min(size, 17)
        result = bytes(self.pending[:size])
        del self.pending[:size]
        return result

    def close(self):
        self.closed = True

    def request(self):
        raw = self.sent[1]
        length = raw[1] & 127
        offset = 2
        if length == 126:
            length = struct.unpack("!H", raw[2:4])[0]
            offset = 4
        mask = raw[offset:offset + 4]
        payload = raw[offset + 4:offset + 4 + length]
        return json.loads(bytes(value ^ mask[i % 4] for i, value in enumerate(payload)))


class T3WebSocketConfigTests(unittest.TestCase):
    @staticmethod
    def response(value):
        return {"_tag": "Exit", "requestId": "1", "exit": {"_tag": "Success", "value": value}}

    def test_effect_json_single_and_batched_fragmented_responses(self):
        for fragmented in (False, True):
            config = {"keybindings": [], "issues": []}
            connection = FakeSocket([{"_tag": "Pong"}, self.response(config)], fragmented=fragmented)
            result = PROJECTOR.websocket_server_config("http://127.0.0.1:12345", "private-ticket", lambda *_, **__: connection)
            self.assertEqual(result, config)
            self.assertTrue(connection.closed)
            self.assertEqual(connection.request(), {"_tag": "Request", "id": "1", "tag": "server.getConfig", "payload": {}, "headers": []})
            self.assertTrue(connection.sent[1][1] & 0x80)  # Client frames are masked.
            self.assertIn(b"orchestrationProtocol=2", connection.sent[0])

    def test_protocol_failures_close_socket_and_never_accept_unverified_config(self):
        cases = [FakeSocket(self.response({}), invalid_handshake=True),
                 FakeSocket({"_tag": "Exit", "requestId": "different", "exit": {"_tag": "Success", "value": {}}}),
                 FakeSocket({"_tag": "Exit", "requestId": "1", "exit": {"_tag": "Failure", "cause": "PRIVATE ERROR"}}),
                 FakeSocket(payload=b"not-json")]
        for connection in cases:
            with self.assertRaises(Exception):
                PROJECTOR.websocket_server_config("http://127.0.0.1:12345", "private-ticket", lambda *_, **__: connection)
            self.assertTrue(connection.closed)

    def test_non_loopback_peer_is_rejected(self):
        connection = FakeSocket(self.response({}))
        with patch.object(connection, "getpeername", return_value=("192.0.2.1", 12345)), self.assertRaises(PROJECTOR.SafeError):
            PROJECTOR.websocket_server_config("http://localhost:12345", "ticket", lambda *_, **__: connection)
        self.assertTrue(connection.closed)
        self.assertEqual(connection.sent, [])

    def test_configuration_frame_payload_is_bounded(self):
        connection = FakeSocket(payload=b"x" * (PROJECTOR.MAX_BYTES + 1))
        with self.assertRaises(PROJECTOR.SafeError) as error:
            PROJECTOR.websocket_server_config("http://127.0.0.1:12345", "ticket", lambda *_, **__: connection)
        self.assertEqual(error.exception.kind, "PayloadLimit")
        self.assertTrue(connection.closed)


if __name__ == "__main__":
    mutation_spec = importlib.util.spec_from_file_location("t3_shortcut_mutation_tests", pathlib.Path(__file__).with_name("T3ShortcutMutationTests.py"))
    mutation_fixtures = importlib.util.module_from_spec(mutation_spec)
    mutation_spec.loader.exec_module(mutation_fixtures)
    ShortcutMutationTests = mutation_fixtures.ShortcutMutationTests
    unittest.main()
