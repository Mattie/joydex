"""Finite SSH-side metadata reader. No conversation bodies leave this process.

The separate setup/remove modes are only invoked by an explicit settings action.
"""
import datetime
import contextlib
import base64
import hashlib
import itertools
import json
import os
import pathlib
import re
import selectors
import signal
import socket
import struct
import subprocess
import sys
import time
import urllib.parse
import urllib.request
import urllib.error

MAX_BYTES = 8 * 1024 * 1024
MAX_THREADS = 1000
MAX_DETAILS = 128
MAX_REQUESTS = 128
COMMANDS = ("chat.new", "thread.previous", "thread.next", "thread.stop")
MAX_BINDINGS = 256


class SafeError(Exception):
    def __init__(self, kind, reason):
        self.kind, self.reason = kind, reason


def bounded_bytes(path):
    with path.open("rb") as stream:
        raw = stream.read(MAX_BYTES + 1)
    if len(raw) > MAX_BYTES:
        raise SafeError("PayloadLimit", "The environment response exceeds the size limit.")
    return raw


def bounded_json(path, default=None):
    if not path.exists() and default is not None:
        return default
    return json.loads(bounded_bytes(path))


def label(value):
    if not isinstance(value, str):
        return None
    return " ".join(value.split())[:240] or None


def identifier(value):
    if not isinstance(value, str) or not value or len(value) > 512 or any(ord(c) < 32 for c in value):
        raise SafeError("InvalidMetadata", "The environment returned an invalid identifier.")
    return value


def timestamp(value):
    if not isinstance(value, str):
        return None
    try:
        parsed = datetime.datetime.fromisoformat(value.replace("Z", "+00:00"))
        return parsed.isoformat() if parsed.tzinfo and parsed.timestamp() > 0 else None
    except (ValueError, OverflowError):
        return None


def cli(executable, arguments, timeout):
    """Drain both pipes with a shared byte cap; kill only this process group."""
    process = subprocess.Popen([str(executable)] + arguments, stdin=subprocess.DEVNULL,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE, start_new_session=True)
    selector = selectors.DefaultSelector()
    output = bytearray()
    total = 0
    end = time.monotonic() + timeout
    for pipe in (process.stdout, process.stderr):
        selector.register(pipe, selectors.EVENT_READ)
    try:
        while selector.get_map():
            if time.monotonic() >= end:
                raise SafeError("CliTimeout", "The T3 command timed out.")
            for key, _ in selector.select(min(0.1, max(0, end - time.monotonic()))):
                chunk = os.read(key.fileobj.fileno(), 65536)
                if not chunk:
                    selector.unregister(key.fileobj)
                    continue
                total += len(chunk)
                if total > MAX_BYTES:
                    raise SafeError("PayloadLimit", "The T3 command exceeded the size limit.")
                if key.fileobj is process.stdout:
                    output.extend(chunk)
        process.wait(timeout=max(0.01, end - time.monotonic()))
        if process.returncode:
            raise SafeError("CliFailed", "The T3 command failed; private command output was withheld.")
        return bytes(output)
    finally:
        selector.close()
        if process.poll() is None:
            os.killpg(process.pid, signal.SIGKILL)
        process.wait()
        process.stdout.close()
        process.stderr.close()


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        raise SafeError("RedirectRejected", "The loopback API attempted a redirect.")


def loopback_origin(runtime):
    origin = runtime.get("origin", "")
    parsed = urllib.parse.urlsplit(origin)
    if (parsed.scheme != "http" or parsed.hostname not in ("127.0.0.1", "localhost", "::1")
            or parsed.username or parsed.password or parsed.query or parsed.fragment or parsed.path not in ("", "/")):
        raise SafeError("LoopbackRequired", "The environment API must use its own loopback HTTP origin.")
    return origin


def websocket_server_config(origin, ticket, connect=None):
    """One finite server.getConfig call, using Effect 4 Json serialization, not JSON-RPC.

    Envelope checked against effect@4.0.5 src/rpc/RpcMessage.ts and RpcSerialization.ts.
    T3 routes/ticket and server.getConfig checked against release 2359643cffa6.
    """
    parsed = urllib.parse.urlsplit(origin)
    deadline = time.monotonic() + 5
    connection = (connect or socket.create_connection)((parsed.hostname, parsed.port or 80), timeout=3)
    total = 0
    def exact(size):
        nonlocal total
        if size < 0 or size > MAX_BYTES or total + size > MAX_BYTES:
            raise SafeError("PayloadLimit", "The configuration response exceeds its size limit.")
        result = bytearray()
        while len(result) < size:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise SafeError("ReadTimeout", "The configuration read reached its deadline.")
            connection.settimeout(min(3, remaining))
            chunk = connection.recv(size - len(result))
            if not chunk:
                raise ValueError("closed")
            result.extend(chunk)
        total += size
        return bytes(result)
    def send(opcode, payload):
        if len(payload) > 4096:
            raise ValueError("outgoing limit")
        mask = os.urandom(4)
        length = len(payload)
        header = bytes([0x80 | opcode, 0x80 | length]) if length < 126 else bytes([0x80 | opcode, 0xFE]) + struct.pack("!H", length)
        connection.sendall(header + mask + bytes(value ^ mask[i % 4] for i, value in enumerate(payload)))
    try:
        import ipaddress
        if not ipaddress.ip_address(connection.getpeername()[0]).is_loopback:
            raise SafeError("LoopbackRequired", "The configuration connection must stay on loopback.")
        key = base64.b64encode(os.urandom(16)).decode("ascii")
        route = "/ws?orchestrationProtocol=2&wsTicket=" + urllib.parse.quote(identifier(ticket), safe="")
        handshake = ("GET " + route + " HTTP/1.1\r\nHost: " + parsed.netloc + "\r\nUpgrade: websocket\r\n"
                     "Connection: Upgrade\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: " + key + "\r\n\r\n")
        connection.sendall(handshake.encode("ascii"))
        headers = bytearray()
        while not headers.endswith(b"\r\n\r\n"):
            if len(headers) >= 16384:
                raise ValueError("header limit")
            headers.extend(exact(1))
        lines = headers.decode("ascii").split("\r\n")
        if lines[0].split(" ", 2)[1] != "101":
            raise ValueError("upgrade rejected")
        values = {name.lower(): value.strip() for name, value in (line.split(":", 1) for line in lines[1:] if ":" in line)}
        expected = base64.b64encode(hashlib.sha1((key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").encode()).digest()).decode()
        if (values.get("sec-websocket-accept", "") != expected or values.get("upgrade", "").lower() != "websocket"
                or "upgrade" not in [value.strip().lower() for value in values.get("connection", "").split(",")]):
            raise ValueError("invalid handshake")
        # Constant method and payload; no write command can be supplied by callers.
        send(1, json.dumps({"_tag": "Request", "id": "1", "tag": "server.getConfig", "payload": {}, "headers": []}, separators=(",", ":")).encode())
        message = bytearray()
        fragmented = False
        for _ in range(64):
            first, second = exact(2)
            final, opcode = bool(first & 0x80), first & 15
            if first & 0x70 or second & 0x80:
                raise ValueError("unsupported websocket flags")
            length = second & 127
            if length == 126:
                length = struct.unpack("!H", exact(2))[0]
            elif length == 127:
                length = struct.unpack("!Q", exact(8))[0]
            payload = exact(length)
            if opcode in (8, 9, 10):
                if not final or length > 125 or opcode == 8:
                    raise ValueError("closed websocket")
                if opcode == 9:
                    send(10, payload)
                continue
            if opcode not in (0, 1) or (opcode == 0 and not fragmented) or (opcode == 1 and fragmented):
                raise ValueError("unsupported websocket frame")
            message.extend(payload)
            fragmented = not final
            if not final:
                continue
            decoded = json.loads(message)
            message.clear()
            envelopes = decoded if isinstance(decoded, list) else [decoded]
            if len(envelopes) > 64:
                raise ValueError("envelope limit")
            for envelope in envelopes:
                if envelope.get("_tag") == "Pong":
                    continue
                if envelope.get("_tag") != "Exit" or str(envelope.get("requestId")) != "1":
                    raise ValueError("unsupported effect response")
                exit_value = envelope.get("exit", {})
                if exit_value.get("_tag") != "Success" or not isinstance(exit_value.get("value"), dict):
                    raise ValueError("config call failed")
                return exit_value["value"]
        raise ValueError("frame limit")
    finally:
        connection.close()


def read_effective_bindings(root, executable):
    runtime_path = root / "server-runtime.json"
    runtime_raw = bounded_bytes(runtime_path)
    origin = loopback_origin(json.loads(runtime_raw))
    credential = json.loads(cli(executable, ["auth", "session", "issue", "--scope", "orchestration:read", "--ttl", "5m",
                                          "--label", "Joydex shortcut capability reader", "--json"], 5))
    try:
        if credential.get("scopes") != ["orchestration:read"]:
            raise SafeError("UnexpectedScopes", "The temporary credential did not have exactly read-only permissions.")
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
        def issue(path):
            request = urllib.request.Request(origin.rstrip("/") + path, data=b"", method="POST",
                headers={"Authorization": "Bearer " + credential["token"]})
            with opener.open(request, timeout=3) as response:
                raw = response.read(MAX_BYTES + 1)
            if len(raw) > MAX_BYTES:
                raise ValueError("ticket limit")
            return json.loads(raw)["ticket"]
        try:
            ticket = issue("/api/auth/websocket-ticket/rpc")
        except urllib.error.HTTPError as error:
            if error.code != 404:
                raise
            ticket = issue("/api/auth/websocket-ticket")
        config = websocket_server_config(origin, ticket)
        if bounded_bytes(runtime_path) != runtime_raw:
            raise SafeError("RuntimeChanged", "The environment restarted during the configuration read.")
        if pathlib.Path(config.get("keybindingsConfigPath", "")).resolve() != (root / "keybindings.json").resolve():
            raise ValueError("configuration path mismatch")
        return config
    finally:
        try:
            cli(executable, ["auth", "session", "revoke", identifier(credential.get("sessionId"))], 5)
        except Exception:
            raise SafeError("RevokeFailed", "The temporary read grant could not be revoked; it expires within five minutes.") from None
        finally:
            credential.clear()


def read_metadata(root, executable, options):
    environment_id = identifier(bounded_bytes(root / "environment-id").decode().strip())
    expected = options.get("environmentId") or ""
    if expected and environment_id != expected:
        raise SafeError("EnvironmentMismatch", "The discovered environment does not match the saved identity.")
    if options.get("mode") == "read" and not expected:
        raise SafeError("EnvironmentRequired", "Test the connection and save its environment identity first.")
    runtime_path = root / "server-runtime.json"
    runtime = bounded_json(runtime_path)
    origin = loopback_origin(runtime)
    started_at = timestamp(runtime.get("startedAt"))
    if not started_at:
        raise SafeError("RuntimeIdentityMissing", "The environment runtime has no valid startup identity.")
    epoch = hashlib.sha256(json.dumps([started_at, runtime.get("pid"), runtime.get("ownerId"), origin]).encode()).hexdigest()
    runtime_before = hashlib.sha256(bounded_bytes(runtime_path)).digest()
    credential = json.loads(cli(executable, ["auth", "session", "issue", "--scope", "orchestration:read",
                                          "--ttl", "5m", "--label", "Joydex metadata reader", "--json"], 5))
    result = None
    revoked = False
    try:
        if credential.get("scopes") != ["orchestration:read"]:
            raise SafeError("UnexpectedScopes", "The temporary credential did not have exactly read-only permissions.")
        token = credential.get("token")
        if not isinstance(token, str) or not token:
            raise SafeError("InvalidCredential", "The temporary credential response was incomplete.")
        deadline = time.monotonic() + 15
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
        def get(path):
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise SafeError("ReadTimeout", "The metadata read reached its deadline.")
            request = urllib.request.Request(origin.rstrip("/") + path,
                headers={"Authorization": "Bearer " + token, "x-t3-orchestration-protocol": "2"})
            with opener.open(request, timeout=min(3, remaining)) as response:
                raw = response.read(MAX_BYTES + 1)
            if len(raw) > MAX_BYTES:
                raise SafeError("PayloadLimit", "The environment response exceeds the size limit.")
            return json.loads(raw)
        shell = get("/api/orchestration/shell?omitIdleSubagents=true")
        threads = shell.get("threads")
        sequence = shell.get("snapshotSequence")
        if not isinstance(threads, list) or not isinstance(sequence, int) or isinstance(sequence, bool) or sequence < 0:
            raise SafeError("UnsupportedProtocol", "The environment did not return a compatible protocol 2 shell.")
        complete = len(threads) <= MAX_THREADS
        threads = threads[:MAX_THREADS]
        projects = {p["id"]: label(p.get("title")) for p in shell.get("projects", [])[:MAX_THREADS]}
        tracked = options.get("knownThreadIds", [])
        if len(tracked) > MAX_DETAILS:
            complete = False
        tracked = set(identifier(t) for t in tracked[:MAX_DETAILS])
        rows = {identifier(t["id"]): t for t in threads if not t.get("deletedAt")}
        # Tracked rows take priority over newly discovered pending rows within the detail budget.
        rows = {thread_id: rows.get(thread_id, {"id": thread_id}) for thread_id in sorted(tracked)} | rows
        for thread_id in tracked:
            rows.setdefault(thread_id, {"id": thread_id})
        tasks, detail_count = [], 0
        for thread_id, thread in rows.items():
            selection = thread.get("modelSelection") or {}
            task = {"id": thread_id, "runId": thread.get("activeRunId") or thread.get("latestRunId"),
                    "status": thread.get("activityRunStatus") or thread.get("status", "unknown"),
                    "startedAt": timestamp(thread.get("activityRunStartedAt") or thread.get("latestRunStartedAt")),
                    "completedAt": timestamp(thread.get("latestRunCompletedAt")),
                    "latestUserAuthoredMessageAt": timestamp(thread.get("latestUserAuthoredMessageAt")),
                    "readComplete": "status" in thread, "requestsComplete": False,
                    "pendingRequests": [], "resolvedQuestions": [], "title": label(thread.get("title")),
                    "project": projects.get(thread.get("projectId")),
                    "provider": label(thread.get("providerInstanceId") or selection.get("instanceId")),
                    "model": label(selection.get("model"))}
            live_statuses = ("queued", "preparing", "starting", "running", "waiting")
            needs_live_proof = task["status"] in live_statuses and (not task["runId"] or not task["startedAt"])
            if thread_id in tracked or thread.get("pendingRuntimeRequest") or needs_live_proof:
                if detail_count >= MAX_DETAILS:
                    task["readComplete"] = False
                    complete = False
                else:
                    detail_count += 1
                    try:
                        detail = get("/api/orchestration/threads/" + urllib.parse.quote(thread_id, safe=""))
                        projection = detail["projection"]
                        native = projection["thread"]
                        if native.get("id") != thread_id:
                            raise ValueError("identity")
                        requests, nodes, runs = projection["runtimeRequests"], projection["nodes"], projection["runs"]
                        if any(not isinstance(v, list) for v in (requests, nodes, runs)):
                            raise ValueError("shape")
                        if len(requests) > MAX_REQUESTS or len(nodes) > 10000 or len(runs) > 10000:
                            raise ValueError("limit")
                        node_runs = {n["id"]: n.get("runId") for n in nodes}
                        request_complete = not detail.get("hasMoreHistory", False)
                        for req in requests:
                            capability = req.get("responseCapability", {}).get("type")
                            if req.get("status") not in ("pending", "resolved"):
                                continue
                            request_run = node_runs.get(req.get("nodeId"))
                            if not request_run:
                                request_complete = False
                                continue
                            projected = {"id": identifier(req["id"]), "runId": identifier(request_run),
                                         "createdAt": timestamp(req.get("createdAt")), "blocking": capability != "message"}
                            if req["status"] == "pending":
                                task["pendingRequests"].append(projected)
                            elif capability == "message":
                                task["resolvedQuestions"].append(projected)
                        selected_run = next((run for run in runs if run.get("id") == task["runId"]), None)
                        if not task["runId"] and needs_live_proof:
                            active_runs = [run for run in runs if run.get("status") in live_statuses]
                            if len(active_runs) == 1:
                                selected_run = active_runs[0]
                                task["runId"] = identifier(selected_run["id"])
                        if selected_run:
                            # Compact-shell timestamps are optional on compatible hosts.
                            # Full detail certifies only this exact selected run, never a
                            # different run that merely happens to have the latest ordinal.
                            task["startedAt"] = timestamp(selected_run.get("startedAt") or selected_run.get("requestedAt")) or task["startedAt"]
                            task["completedAt"] = timestamp(selected_run.get("completedAt")) or task["completedAt"]
                            if isinstance(selected_run.get("status"), str):
                                task["status"] = selected_run["status"]
                        # Direct reads recover tracked threads omitted from the compact shell.
                        if "status" not in thread:
                            latest = max(runs, key=lambda r: r.get("ordinal", 0), default={})
                            task.update(runId=latest.get("id"), status=latest.get("status", "unknown"),
                                        startedAt=timestamp(latest.get("startedAt")), completedAt=timestamp(latest.get("completedAt")),
                                        title=label(native.get("title")), project=projects.get(native.get("projectId")))
                        task["requestsComplete"] = request_complete
                        task["readComplete"] = True
                        if needs_live_proof and (not task["runId"] or not task["startedAt"]):
                            task["readComplete"] = False
                            task["requestsComplete"] = False
                            complete = False
                        # The detail may be newer than the initial shell. Never move its cursor backwards.
                        detail_sequence = detail.get("snapshotSequence")
                        if isinstance(detail_sequence, int) and not isinstance(detail_sequence, bool):
                            sequence = max(sequence, detail_sequence)
                    except Exception:
                        task["readComplete"] = False
                        complete = False
            tasks.append(task)
        if hashlib.sha256(bounded_bytes(runtime_path)).digest() != runtime_before:
            raise SafeError("RuntimeChanged", "The environment restarted during the read; retry the connection.")
        result = {"success": True, "environmentId": environment_id, "sourceEpoch": epoch, "sequence": sequence,
                  "capturedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(), "isComplete": complete,
                  "tasks": tasks, "bindingFingerprint": optional_fingerprint(root / "keybindings.json")}
    finally:
        try:
            session_id = identifier(credential.get("sessionId"))
            cli(executable, ["auth", "session", "revoke", session_id], 5)
            revoked = True
        except Exception:
            raise SafeError("RevokeFailed", "The temporary read grant could not be revoked; it expires within five minutes.") from None
        finally:
            credential.clear()
    if not revoked:
        raise SafeError("RevokeFailed", "The temporary read grant could not be revoked; it expires within five minutes.")
    return result


def fingerprint(path):
    return hashlib.sha256(bounded_bytes(path) if path.exists() else b"[]").hexdigest()


def optional_fingerprint(path):
    try:
        return fingerprint(path)
    except Exception:
        # Monitoring does not depend on access to optional shortcut configuration.
        return None


def chord(value):
    aliases = {"mod": "ctrl", "control": "ctrl", "option": "alt", "cmd": "meta"}
    return "+".join(sorted({aliases.get(token.strip(), token.strip()) for token in str(value).lower().split("+")}))


def when_expression(node, depth=0):
    if depth > 32 or not isinstance(node, dict):
        raise ValueError("unknown context expression")
    kind = node.get("type")
    if kind == "identifier":
        name = node.get("name", "")
        if not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_.-]*", name):
            raise ValueError("invalid context identifier")
        return name
    if kind == "not":
        return "!(" + when_expression(node.get("node"), depth + 1) + ")"
    if kind in ("and", "or"):
        return "(" + when_expression(node.get("left"), depth + 1) + (" && " if kind == "and" else " || ") + when_expression(node.get("right"), depth + 1) + ")"
    raise ValueError("unknown context expression")


def unresolved_rule(resolved):
    shortcut = resolved["shortcut"]
    key = shortcut["key"]
    if not isinstance(key, str) or not key or len(key) > 64:
        raise ValueError("invalid shortcut")
    modifiers = (("metaKey", "meta"), ("ctrlKey", "ctrl"), ("altKey", "alt"), ("shiftKey", "shift"), ("modKey", "mod"))
    if any(not isinstance(shortcut.get(field), bool) for field, _ in modifiers):
        raise ValueError("unknown modifiers")
    tokens = [token for field, token in modifiers if shortcut[field]] + [("space" if key == " " else key)]
    rule = {"key": "+".join(tokens), "command": resolved["command"]}
    if "whenAst" in resolved:
        rule["when"] = when_expression(resolved["whenAst"])
    return rule


def equivalent_precedence(before, after, touched_keys):
    """Prove unchanged winners for preserved chords in every bounded boolean context."""
    def names(node):
        if not node:
            return set()
        kind = node["type"]
        if kind == "identifier":
            return {node["name"]}
        if kind == "not":
            return names(node["node"])
        return names(node["left"]) | names(node["right"])
    def evaluate(node, context):
        if not node:
            return True
        kind = node["type"]
        if kind == "identifier":
            return context[node["name"]]
        if kind == "not":
            return not evaluate(node["node"], context)
        if kind == "and":
            return evaluate(node["left"], context) and evaluate(node["right"], context)
        return evaluate(node["left"], context) or evaluate(node["right"], context)
    for key in touched_keys:
        prior = [r for r in before if chord(unresolved_rule(r)["key"]) == key]
        later = [r for r in after if chord(unresolved_rule(r)["key"]) == key]
        variables = sorted(set().union(*(names(r.get("whenAst")) for r in prior + later)))
        if len(variables) > 10:
            return False
        for bits in itertools.product((False, True), repeat=len(variables)):
            context = dict(zip(variables, bits))
            old = [r["command"] for r in prior if evaluate(r.get("whenAst"), context)]
            new = [r["command"] for r in later if evaluate(r.get("whenAst"), context)]
            if (old[-1:] or None) != (new[-1:] or None):
                return False
    return True


def rule_metadata(rules):
    """Unknown JSON properties stay on disk; only technical shortcut fields cross SSH."""
    result = []
    for rule in rules:
        if not isinstance(rule, dict) or rule.get("command") not in COMMANDS:
            continue
        key, when = rule.get("key"), rule.get("when")
        if not isinstance(key, str) or not key or len(key) > 128 or any(ord(c) < 32 for c in key):
            continue
        if when is not None and (not isinstance(when, str) or len(when) > 256 or any(ord(c) < 32 for c in when)):
            continue
        projected = {"command": rule["command"], "key": key}
        if when is not None:
            projected["when"] = when
        result.append(projected)
    return result[:128]


def write_private(path, raw):
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "wb") as stream:
        stream.write(raw)
        stream.flush()
        os.fsync(stream.fileno())


@contextlib.contextmanager
def shortcut_mutation_lock(root):
    """Serialize explicit setup/remove processes, without waiting or affecting reads."""
    try:
        import fcntl  # Remote mutation is Linux-only; offline Windows fixtures replace this context.
    except ImportError:
        raise SafeError("UnsupportedHost", "Shortcut mutation requires a Linux host with advisory file locking.")
    path = root / ".joydex-keybindings.lock"
    fd = os.open(path, os.O_RDWR | os.O_CREAT | getattr(os, "O_NOFOLLOW", 0), 0o600)
    try:
        try:
            fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise SafeError("Busy", "Another shortcut setup or removal is in progress; no shortcuts were changed.")
        yield
    finally:
        os.close(fd)  # Closing releases flock. Keep its inode stable for the next process.


def sync_directory(root):
    if os.name == "posix":
        fd = os.open(root, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
        try:
            os.fsync(fd)
        finally:
            os.close(fd)


def optional_private_bytes(path):
    if path.is_symlink():
        raise SafeError("InvalidBindings", "Shortcut mutation does not replace symbolic-link configuration files.")
    return bounded_bytes(path) if path.exists() else None


def replace_private(path, raw):
    temporary = path.parent / (".joydex-keybindings-write-" + str(time.time_ns()))
    write_private(temporary, raw)
    try:
        os.replace(temporary, path)
        sync_directory(path.parent)
    finally:
        if temporary.exists():
            temporary.unlink()


def recover_shortcut_transaction(root):
    """Finish only an exact prepared profile/manifest pair; ambiguous edits stay untouched."""
    transaction_path = root / ".joydex-keybindings-transaction.json"
    raw = optional_private_bytes(transaction_path)
    if raw is None:
        return
    try:
        transaction = json.loads(raw)
        if transaction.get("schemaVersion") != 1 or not isinstance(transaction.get("beforeExists"), bool):
            raise ValueError("transaction schema")
        before_manifest = transaction.get("beforeManifest")
        before_manifest = None if before_manifest is None else base64.b64decode(before_manifest, validate=True)
        after_manifest = base64.b64decode(transaction["afterManifest"], validate=True)
        for manifest in (before_manifest, after_manifest):
            if manifest is not None:
                validate_owned_rules(json.loads(manifest))
        before_hash, after_hash = transaction["beforeFingerprint"], transaction["afterFingerprint"]
        if not all(isinstance(value, str) and re.fullmatch(r"[0-9a-f]{64}", value) for value in (before_hash, after_hash)):
            raise ValueError("transaction fingerprints")
    except Exception:
        raise SafeError("InvalidBindings", "The shortcut transaction is invalid; review the private recovery files before retrying.")
    path, manifest_path = root / "keybindings.json", root / ".joydex-keybinding-additions.json"
    profile = optional_private_bytes(path)
    manifest = optional_private_bytes(manifest_path)
    current_hash = hashlib.sha256(profile if profile is not None else b"[]").hexdigest()
    if manifest not in (before_manifest, after_manifest):
        raise SafeError("BindingsChanged", "The shortcut ownership record changed during recovery; no shortcuts were changed.")
    if current_hash == after_hash and profile is not None:
        desired_manifest = after_manifest
    elif current_hash == before_hash and (profile is not None) == transaction["beforeExists"]:
        desired_manifest = before_manifest
    else:
        raise SafeError("BindingsChanged", "The keybinding file changed during an unfinished shortcut transaction; review the private recovery files before retrying.")
    if (optional_private_bytes(path) != profile or optional_private_bytes(manifest_path) != manifest
            or optional_private_bytes(transaction_path) != raw):
        raise SafeError("BindingsChanged", "Shortcut recovery files changed during validation; no shortcuts were changed.")
    if manifest != desired_manifest:
        if desired_manifest is None:
            manifest_path.unlink()
            sync_directory(root)
        else:
            replace_private(manifest_path, desired_manifest)
    if optional_private_bytes(path) != profile or optional_private_bytes(manifest_path) != desired_manifest:
        raise SafeError("BindingsChanged", "Shortcut files changed during recovery; the private transaction was retained for review.")
    transaction_path.unlink()
    sync_directory(root)


def validate_owned_rules(manifest):
    if not isinstance(manifest, dict):
        raise SafeError("InvalidBindings", "The shortcut additions manifest is invalid; review it before retrying.")
    owned = manifest.get("rules", [])
    if (not isinstance(owned, list) or len(owned) > 128 or
            any(not isinstance(rule, dict) or rule.get("command") not in COMMANDS for rule in owned)):
        raise SafeError("InvalidBindings", "The shortcut additions manifest is invalid; review it before retrying.")
    return owned


def setup_shortcuts(root, remove, effective_config=None):
    with shortcut_mutation_lock(root):
        return setup_shortcuts_locked(root, remove, effective_config)


def setup_shortcuts_locked(root, remove, effective_config=None, expected_environment=None):
    path = root / "keybindings.json"
    manifest_path = root / ".joydex-keybinding-additions.json"
    # Invalid effective capabilities must not repair, claim or write any pending state.
    if not remove:
        try:
            if not effective_config or effective_config.get("issues"):
                raise ValueError("configuration unavailable or invalid")
            effective = effective_config["keybindings"]
            if not isinstance(effective, list) or len(effective) > MAX_BINDINGS:
                raise ValueError("configuration limit")
            effective_rules = [unresolved_rule(rule) for rule in effective]
        except Exception:
            rules = bounded_json(path, [])
            return {"success": False, "errorClass": "UnsupportedShortcuts",
                    "reason": "The server's effective keybindings could not be verified; no shortcuts were changed.",
                    "bindingFingerprint": fingerprint(path), "rules": rule_metadata(rules),
                    "diagnostics": ["Read-only monitoring remains available. Shortcut setup requires a compatible server.getConfig response."]}
    recover_shortcut_transaction(root)
    original_bytes = optional_private_bytes(path)
    original = original_bytes if original_bytes is not None else b"[]"
    rules = json.loads(original)
    if not isinstance(rules, list) or len(rules) > MAX_BINDINGS:
        raise SafeError("InvalidBindings", "The existing keybinding rules are not a compatible JSON array.")
    manifest_bytes = optional_private_bytes(manifest_path)
    manifest = json.loads(manifest_bytes) if manifest_bytes is not None else {"rules": []}
    owned = validate_owned_rules(manifest)
    diagnostics, added, removed = [], [], []
    if remove:
        remaining_owned = []
        for rule in owned:
            # Remove only an exact, unique match. User edits and duplicates are retained.
            matches = [i for i, r in enumerate(rules) if r == rule]
            if len(matches) == 1:
                rules.pop(matches[0])
                if rule["command"] not in removed:
                    removed.append(rule["command"])
            else:
                diagnostics.append("A changed or ambiguous added rule was retained and Joydex ownership released: " + str(rule.get("command")))
    else:
        used = {chord(r.get("key", "")) for r in rules if isinstance(r, dict)} | {chord(r["key"]) for r in effective_rules}
        remaining_owned = [r for r in owned if r in rules]
        custom_commands = {r.get("command") for r in rules if isinstance(r, dict) and isinstance(r.get("command"), str)}
        for command in COMMANDS:
            matches = [r for r in rules if isinstance(r, dict) and r.get("command") == command]
            if matches:
                last = matches[-1]
                projected = rule_metadata([last])
                diagnostics.append(command + (": last configured rule " + projected[0]["key"][:80] + " when " + projected[0].get("when", "always")[:160]
                                             if projected else ": unsupported existing rule retained") + "; active context unverified.")
                continue
            key = next(("ctrl+alt+shift+" + k for k in "1234567890" if chord("ctrl+alt+shift+" + k) not in used), None)
            if not key:
                raise SafeError("BindingConflict", "No conflict-free dedicated shortcut is available.")
            rule = {"key": key, "command": command, "when": "isDesktop"}
            rules.append(rule)
            used.add(chord(key))
            remaining_owned.append(rule)
            added.append(command)
            diagnostics.append(command + ": dedicated " + key + " when isDesktop; prior effective chords retained.")
        preserved = [r for r in effective_config["keybindings"] if r["command"] in added]
        # Keep all original file rules in their original relative order and precedence.
        after_order = ([r for r in effective_config["keybindings"] if r["command"] not in custom_commands and r["command"] not in added]
                       + preserved + [r for r in effective_config["keybindings"] if r["command"] in custom_commands])
        preserved_keys = {chord(unresolved_rule(r)["key"]) for r in preserved}
        if not equivalent_precedence(effective_config["keybindings"], after_order, preserved_keys):
            raise SafeError("BindingConflict", "Preserving existing shortcut precedence could not be verified; no shortcuts were changed.")
        preserved_rules = [unresolved_rule(r) for r in preserved]
        rules = preserved_rules + rules
        remaining_owned.extend(preserved_rules)
        if len(rules) > MAX_BINDINGS or len(effective_rules) + len(added) > MAX_BINDINGS:
            raise SafeError("BindingConflict", "Adding shortcuts would exceed the server's rule limit; no shortcuts were changed.")
        if sum(isinstance(rule, dict) and rule.get("command") in COMMANDS for rule in rules) > 128:
            raise SafeError("BindingConflict", "The shortcut capability result would exceed its supported rule limit; no shortcuts were changed.")
    journal = json.dumps({"rules": remaining_owned}, indent=2).encode() + b"\n"
    def check_unchanged():
        if optional_private_bytes(path) != original_bytes or optional_private_bytes(manifest_path) != manifest_bytes:
            raise SafeError("BindingsChanged", "The keybinding file or ownership record changed during setup; no shortcuts were changed.")
        if expected_environment is not None and identifier(bounded_bytes(root / "environment-id").decode().strip()) != expected_environment:
            raise SafeError("EnvironmentMismatch", "The environment changed during shortcut setup; no shortcuts were changed.")
    if added or removed:
        replacement = json.dumps(rules, ensure_ascii=False, indent=2).encode() + b"\n"
        check_unchanged()
        # A prepared transaction does not claim ownership. Commit the key file first;
        # a later explicit action can finish the exact matching manifest after a crash.
        backup = root / (".joydex-keybindings-backup-" + str(time.time_ns()) + ".json")
        write_private(backup, original)
        transaction_path = root / ".joydex-keybindings-transaction.json"
        transaction = json.dumps({"schemaVersion": 1, "beforeExists": original_bytes is not None,
            "beforeFingerprint": hashlib.sha256(original).hexdigest(), "afterFingerprint": hashlib.sha256(replacement).hexdigest(),
            "beforeManifest": None if manifest_bytes is None else base64.b64encode(manifest_bytes).decode(),
            "afterManifest": base64.b64encode(journal).decode()}).encode() + b"\n"
        temporary = root / (".joydex-keybindings-write-" + str(time.time_ns()))
        write_private(temporary, replacement)
        commit_attempted = False
        transaction_published = False
        try:
            check_unchanged()
            replace_private(transaction_path, transaction)
            transaction_published = True
            check_unchanged()
            commit_attempted = True
            os.replace(temporary, path)
            sync_directory(root)
            replace_private(manifest_path, journal)
            if optional_private_bytes(path) != replacement or optional_private_bytes(manifest_path) != journal:
                raise SafeError("BindingsChanged", "Shortcut files changed during commit; the private transaction was retained for review.")
            transaction_path.unlink()
            sync_directory(root)
        finally:
            if temporary.exists():
                temporary.unlink()
            if transaction_published and not commit_attempted and optional_private_bytes(transaction_path) == transaction:
                transaction_path.unlink()
                sync_directory(root)
    elif remaining_owned != owned:
        # Releasing ownership cannot remove user rules, and needs only one atomic file.
        check_unchanged()
        replace_private(manifest_path, journal)
    return {"success": True, "bindingFingerprint": fingerprint(path), "addedCommands": added,
            "removedCommands": removed, "diagnostics": diagnostics,
            "rules": rule_metadata(rules),
            "capabilitySource": "T3 server.getConfig effective keybindings read with orchestration:read" if not remove else "Private exact-addition manifest",
            "contextDiagnostic": "Active Electron environment and keymap are not verified."}


def main(options, home=None):
    root = (pathlib.Path(home) if home else pathlib.Path.home()) / ".t3/userdata"
    mode = options.get("mode", "read")
    if mode in ("setup", "remove"):
        with shortcut_mutation_lock(root):
            environment_id = identifier(bounded_bytes(root / "environment-id").decode().strip())
            if not options.get("environmentId") or options["environmentId"] != environment_id:
                raise SafeError("EnvironmentMismatch", "The environment does not match the saved identity.")
            if mode == "remove":
                return setup_shortcuts_locked(root, True, expected_environment=environment_id)
            before = fingerprint(root / "keybindings.json")
            try:
                config = read_effective_bindings(root, pathlib.Path.home() / ".local/bin/t3")
            except SafeError as error:
                if error.kind == "RevokeFailed":
                    raise
                config = None
            except Exception:
                config = None
            if fingerprint(root / "keybindings.json") != before or identifier(bounded_bytes(root / "environment-id").decode().strip()) != environment_id:
                raise SafeError("BindingsChanged", "The environment configuration changed during the capability read; retry after reviewing it.")
            return setup_shortcuts_locked(root, False, config, expected_environment=environment_id)
    if mode not in ("read", "test"):
        raise SafeError("InvalidMode", "The requested operation is unsupported.")
    return read_metadata(root, pathlib.Path.home() / ".local/bin/t3", options)


if __name__ == "__main__":
    def deadline_expired(signum, frame):
        raise SafeError("ReadTimeout", "The metadata operation reached its finite deadline.")
    signal.signal(signal.SIGALRM, deadline_expired)
    signal.alarm(25)
    try:
        # Options are appended as a literal by the local transport, never shell arguments.
        result = main(JOYDEX_OPTIONS)
    except SafeError as error:
        result = {"success": False, "errorClass": error.kind, "reason": error.reason}
    except Exception:
        result = {"success": False, "errorClass": "ReadFailed", "reason": "The environment operation failed; private details were withheld."}
    finally:
        signal.alarm(0)
    print(json.dumps(result, separators=(",", ":")))
