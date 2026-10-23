"""Native dot signaling with runtime response checks. Credentials and HTTP bodies stay in memory.

One worker owns one call. Stdio exposes only target metadata, SDP and stop status.
There are no retries for create, attach or stop, and no task or conversation writes.
"""
import base64
import http.client
import json
import os
import queue
import re
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

MAX_BYTES = 256 * 1024


class NoRedirect(urllib.request.HTTPRedirectHandler):
    """Keep credentials on the verified backend and surface redirect failures."""

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class MeasuredHttpsHandler(urllib.request.HTTPSHandler):
    """Collect numeric transport timings without logging requests or credentials."""

    def https_open(self, request):
        timings = request.joydex_timings

        class Connection(http.client.HTTPSConnection):
            def connect(self):
                started = time.perf_counter()
                try:
                    return super().connect()
                finally:
                    timings["connectMs"] = round((time.perf_counter() - started) * 1000)

        return self.do_open(Connection, request, context=self._context)


def run(binary):
    """Authenticate through our owned App Server and serve bounded call commands."""
    environment = os.environ.copy()
    environment.pop("OPENAI_API_KEY", None)
    process = subprocess.Popen(
        [binary, "app-server"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL, text=True, encoding="utf-8", env=environment,
        creationflags=subprocess.CREATE_NO_WINDOW)
    messages = queue.Queue(maxsize=128)

    def reader():
        """Receive auth responses without retaining unbounded notifications."""
        try:
            while line := process.stdout.readline(MAX_BYTES + 1):
                if len(line) > MAX_BYTES:
                    break
                message = json.loads(line)
                if "id" in message:
                    messages.put(message, timeout=1)
        finally:
            messages.put(None)

    def rpc(identifier, method, params):
        """Run a bounded cached-login RPC; never serialize its result to stdout."""
        process.stdin.write(json.dumps({"id": identifier, "method": method, "params": params}) + "\n")
        process.stdin.flush()
        result = messages.get(timeout=20)
        if not result or result.get("id") != identifier or "error" in result:
            raise RuntimeError("CachedLoginUnavailable")
        return result.get("result") or {}

    threading.Thread(target=reader, daemon=True).start()
    opener = urllib.request.build_opener(NoRedirect(), MeasuredHttpsHandler())
    root = None
    call_id = None
    stopped = False
    stop_attempted = False
    allocation_uncertain = False
    try:
        rpc(1, "initialize", {"clientInfo": {"name": "joydex-dot-voice", "version": "1.0"},
                              "capabilities": {"experimentalApi": True}})
        process.stdin.write('{"method":"initialized"}\n')
        process.stdin.flush()
        auth = rpc(2, "getAuthStatus", {"includeToken": True, "refreshToken": False})
        account = rpc(3, "account/read", {"refreshToken": False})
        bearer = auth.get("authToken")
        if auth.get("authMethod") != "chatgpt" or not bearer:
            raise RuntimeError("ChatGPTLoginRequired")
        claims = json.loads(base64.urlsafe_b64decode(bearer.split(".")[1] + "==="))
        identity = claims.get("https://api.openai.com/auth") or {}
        account_id = identity.get("chatgpt_account_id")
        user_id = identity.get("chatgpt_user_id") or identity.get("user_id")
        routing = account.get("workspaceRouting") or {}
        if not account_id or not user_id or routing.get("chatgptAccountId") != account_id:
            raise RuntimeError("AccountIdentityUnavailable")
        if routing.get("backendOrigin") != "https://chatgpt.com" or routing.get("accountRoutingOverride") != "NO_CONSTRAINT":
            raise RuntimeError("UnvalidatedWorkspaceRouting")
        base = "https://chatgpt.com/backend-api/"
        headers = {"Authorization": "Bearer " + bearer, "ChatGPT-Account-Id": account_id,
                   "X-OpenAI-Expected-Account-Id": account_id, "originator": "Joydex",
                   "User-Agent": "Joydex-DirectCall-Canary/0.1", "Accept": "application/json"}

        def http(path, payload=None, post=False):
            """Issue one request with a bounded body and no mutation retry."""
            data = json.dumps(payload).encode("utf-8") if payload is not None else (b"" if post else None)
            request = urllib.request.Request(path, data=data, headers=headers, method="POST" if post else "GET")
            timings = {"connectMs": 0, "headersMs": 0, "bodyMs": 0}
            request.joydex_timings = timings
            if payload is not None:
                request.add_header("Content-Type", "application/json")
            started = time.perf_counter()
            try:
                response = opener.open(request, timeout=20)
            except urllib.error.HTTPError as error:
                response = error
            timings["headersMs"] = max(0, round((time.perf_counter() - started) * 1000) - timings["connectMs"])
            with response:
                started = time.perf_counter()
                body = response.read(MAX_BYTES + 1)
                timings["bodyMs"] = round((time.perf_counter() - started) * 1000)
                if len(body) > MAX_BYTES:
                    raise RuntimeError("ResponseTooLarge")
                return response.status, response.headers, body, timings

        while line := sys.stdin.readline(MAX_BYTES + 1):
            identifier = None
            timings = None
            try:
                if len(line) > MAX_BYTES:
                    raise RuntimeError("CommandTooLarge")
                command = json.loads(line)
                identifier = command["id"]
                operation = command["op"]
                if operation in ("discover", "verify"):
                    if call_id or allocation_uncertain:
                        raise RuntimeError("CallAlreadyAllocated")
                    if operation == "verify":
                        if command["accountId"] != account_id or command["userId"] != user_id:
                            raise RuntimeError("AccountIdentityChanged")
                        path = "tbo/by-thread/" + urllib.parse.quote(command["threadId"], safe="")
                    else:
                        path = "tbo/primary"
                    status, _, body, timings = http(base + path)
                    if status != 200:
                        raise RuntimeError("DotLookupRejected")
                    document = json.loads(body)
                    profile = document["profile"] if "profile" in document else document
                    dot_id = profile["id"]
                    thread_id = command.get("threadId") or document["selection"]["thread_id"]
                    if operation == "verify" and dot_id != command["dotId"]:
                        raise RuntimeError("DotTargetChanged")
                    root = base + "tbo/" + urllib.parse.quote(dot_id, safe="") + "/voice/calls"
                    result = {"accountId": account_id, "userId": user_id, "dotId": dot_id,
                              "threadId": thread_id, "label": profile["display_name"]}
                elif operation == "create":
                    if not root or call_id or allocation_uncertain:
                        raise RuntimeError("CallNotReady")
                    # Even a timeout might allocate remotely; don't permit a second call.
                    allocation_uncertain = True
                    status, response_headers, body, timings = http(root, {"sdp": command["sdp"]}, post=True)
                    candidate = (response_headers.get("Location") or "").split("/")[-1]
                    if re.fullmatch(r"rtc_[^/?#\s]+", candidate):
                        call_id = candidate
                        allocation_uncertain = False
                    if status != 201 or not call_id:
                        # An explicit rejection without an allocation can be safely retried on a later wake.
                        if 400 <= status < 500 and status != 408:
                            allocation_uncertain = False
                        raise RuntimeError("DotCallRejected")
                    result = {"sdp": body.decode("utf-8")}
                elif operation in ("attach", "stop"):
                    if operation == "stop":
                        stop_attempted = True
                    if allocation_uncertain:
                        raise RuntimeError("AllocationUncertain")
                    if call_id and not stopped:
                        status, _, _, timings = http(root + "/" + urllib.parse.quote(call_id, safe="") + "/" + operation, post=True)
                        if status != 200:
                            raise RuntimeError("DotCall" + operation.title() + "Rejected")
                        stopped = operation == "stop"
                    elif operation == "attach":
                        raise RuntimeError("CallNotAllocated")
                    result = {"stopped": stopped or call_id is None}
                else:
                    raise RuntimeError("UnknownCommand")
                print(json.dumps({"id": identifier, "result": result, "httpTiming": timings}), flush=True)
            except Exception as error:
                # Error text may contain HTTP bodies or credentials; emit our own codes only.
                code = str(error) if type(error) is RuntimeError and re.fullmatch(r"[A-Za-z]+", str(error)) else type(error).__name__
                print(json.dumps({"id": identifier, "failure": code}), flush=True)
    finally:
        # Best effort on parent pipe loss; the owner requires an explicit stop acknowledgment.
        if call_id and root and not stopped and not stop_attempted:
            try:
                http(root + "/" + urllib.parse.quote(call_id, safe="") + "/stop", post=True)
            except Exception:
                pass
        process.stdin.close()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)


if __name__ == "__main__":
    try:
        run(sys.argv[1])
    except Exception as error:
        code = str(error) if type(error) is RuntimeError and re.fullmatch(r"[A-Za-z]+", str(error)) else type(error).__name__
        print(json.dumps({"failure": code}), flush=True)
