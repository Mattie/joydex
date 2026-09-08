#!/usr/bin/env python3
"""Run one identity-gated, lifecycle-aware Voice PE UDPPCM physical canary."""

from __future__ import annotations

import argparse
import asyncio
import json
from pathlib import Path
from typing import Any, Callable

from aioesphomeapi import APIClient


COUNTER_NAMES = (
    "Joydex Audio Uplink Frames",
    "Joydex Audio Uplink Dropped Bytes",
    "Joydex Audio Downlink Frames",
    "Joydex Audio Downlink Dropped Bytes",
    "Joydex Audio Downlink Underruns",
    "Joydex Audio Speaker Accepted Bytes",
    "Joydex Audio Speaker Backpressure Events",
    "Joydex Audio Speaker Partial Writes",
    "Joydex Audio UDP Uplink Send Drops",
    "Joydex Audio UDP Invalid Packets",
    "Joydex Audio UDP Foreign Packets",
    "Joydex Audio UDP Downlink Missing Packets",
    "Joydex Audio UDP Downlink Late Packets",
    "Joydex Audio UDP Sessions",
    "Joydex Audio UDP Timeouts",
)

MAX_GAUGE_NAMES = (
    "Joydex Audio Uplink Queue High Water Bytes",
    "Joydex Audio Downlink Queue High Water Bytes",
    "Joydex Audio Downlink Max Loop Gap Milliseconds",
    "Joydex Audio UDP Uplink Send Max Microseconds",
)

MAX_GAUGE_LIMITS = {
    "Joydex Audio Uplink Queue High Water Bytes": 6_400,
    "Joydex Audio Downlink Queue High Water Bytes": 19_200,
    "Joydex Audio Downlink Max Loop Gap Milliseconds": 100,
    "Joydex Audio UDP Uplink Send Max Microseconds": 20_000,
}

QUALIFICATION_SECONDS = 180

TELEMETRY_NAMES = (
    "Joydex Audio Last Socket Errno",
    "Joydex Audio Last Close Reason",
)

CONTROL_NAMES = (
    "Joydex Voice Session State",
    "Joydex Audio Barge In",
    "Joydex Wake Engine Running",
    "Joydex Microphone Capturing",
    "Joydex Audio Connected",
    "Joydex Audio Session Active",
)


def parse_args() -> argparse.Namespace:
    repository = Path(__file__).resolve().parents[2]
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", required=True)
    parser.add_argument("--expected-name", required=True)
    parser.add_argument("--expected-mac", required=True)
    parser.add_argument("--expected-version", required=True)
    parser.add_argument(
        "--direction",
        choices=("speaker-only", "microphone-only", "duplex"),
        required=True,
    )
    parser.add_argument("--seconds", type=int, default=30)
    parser.add_argument("--settle-ms", type=int, default=3000)
    parser.add_argument(
        "--dotnet",
        type=Path,
        default=repository / ".tools" / "dotnet" / "dotnet.exe",
    )
    parser.add_argument(
        "--assembly",
        type=Path,
        default=(
            Path(__file__).resolve().parent
            / "bin"
            / "Release"
            / "net8.0-windows"
            / "Joydex.VoicePeUdpPrototype.dll"
        ),
    )
    args = parser.parse_args()
    if not 1 <= args.seconds <= 3600:
        parser.error("--seconds must be from 1 through 3600")
    if not 0 <= args.settle_ms <= 10_000:
        parser.error("--settle-ms must be from 0 through 10000")
    if not args.dotnet.is_file():
        parser.error(f"Repository-local .NET is missing: {args.dotnet}")
    if not args.assembly.is_file():
        parser.error(f"Build the UDPPCM prototype first: {args.assembly}")
    return args


async def wait_for(
    predicate: Callable[[], bool], description: str, timeout_seconds: float = 15
) -> None:
    deadline = asyncio.get_running_loop().time() + timeout_seconds
    while asyncio.get_running_loop().time() < deadline:
        if predicate():
            return
        await asyncio.sleep(0.2)
    raise RuntimeError(f"Timed out waiting for {description}")


async def run(args: argparse.Namespace) -> dict[str, Any]:
    expected_mac = args.expected_mac.replace(":", "").lower()
    client = APIClient(
        args.host,
        6053,
        None,
        client_info="joydex-udp-pcm-canary",
        expected_name=args.expected_name,
        expected_mac=expected_mac,
    )
    await client.connect(login=True)
    entities_by_name: dict[str, Any] = {}
    states: dict[int, Any] = {}
    process: asyncio.subprocess.Process | None = None
    try:
        info, entities, _ = await client.device_info_and_list_entities()
        actual_mac = (info.mac_address or "").replace(":", "").lower()
        if info.name != args.expected_name or actual_mac != expected_mac:
            raise RuntimeError("Voice PE identity does not match the required name and MAC")
        if info.project_name != "Joydex.Voice PE UDP Prototype":
            raise RuntimeError(f"Unexpected project: {info.project_name}")
        if info.project_version != args.expected_version:
            raise RuntimeError(f"Unexpected firmware version: {info.project_version}")

        entities_by_name = {entity.name: entity for entity in entities}
        required = (*COUNTER_NAMES, *MAX_GAUGE_NAMES, *TELEMETRY_NAMES, *CONTROL_NAMES)
        missing = [name for name in required if name not in entities_by_name]
        if missing:
            raise RuntimeError(f"Required UDPPCM entities are missing: {missing}")

        client.subscribe_states(lambda state: states.__setitem__(state.key, state))
        await asyncio.sleep(6)

        def value(name: str) -> Any:
            state = states.get(entities_by_name[name].key)
            return getattr(state, "state", None)

        async def set_barge_in(enabled: bool) -> None:
            client.switch_command(entities_by_name["Joydex Audio Barge In"].key, enabled)
            await wait_for(
                lambda: value("Joydex Audio Barge In") is enabled,
                f"barge-in={enabled}",
            )

        async def set_session(target: str) -> None:
            client.select_command(entities_by_name["Joydex Voice Session State"].key, target)
            await wait_for(
                lambda: value("Joydex Voice Session State") == target,
                f"session={target}",
            )

        def counters() -> dict[str, int]:
            return {name: int(round(value(name) or 0)) for name in COUNTER_NAMES}

        def max_gauges() -> dict[str, int]:
            return {name: int(round(value(name) or 0)) for name in MAX_GAUGE_NAMES}

        barge_in = args.direction != "speaker-only"
        await set_barge_in(barge_in)
        await set_session("Starting")
        await wait_for(
            lambda: value("Joydex Wake Engine Running") is False,
            "wake inference to pause",
        )
        await wait_for(
            lambda: value("Joydex Microphone Capturing") is True,
            "microphone capture to remain active",
        )
        await wait_for(
            lambda: value("Joydex Audio Connected") is False
            and value("Joydex Audio Session Active") is False,
            "any stale UDPPCM session to expire",
        )
        await asyncio.sleep(5.5)
        before = counters()

        process = await asyncio.create_subprocess_exec(
            str(args.dotnet),
            str(args.assembly),
            "device",
            "--host",
            args.host,
            "--direction",
            args.direction,
            "--seconds",
            str(args.seconds),
            "--settle-ms",
            str(args.settle_ms),
            "--wait-for-start",
            stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
        )
        await wait_for(
            lambda: value("Joydex Audio Connected") is True
            and value("Joydex Audio Session Active") is True,
            "the UDPPCM media session to become active",
        )
        await set_session("Listening")
        if process.stdin is None:
            raise RuntimeError("UDPPCM client start gate has no stdin pipe")
        process.stdin.write(b"START\n")
        await process.stdin.drain()

        timeout_seconds = args.seconds + (args.settle_ms / 1000) + 30
        stdout = b""
        stderr = b""
        failure_detail: str | None = None
        try:
            stdout, stderr = await asyncio.wait_for(
                process.communicate(), timeout=timeout_seconds
            )
        except asyncio.TimeoutError:
            failure_detail = f"UDPPCM client timed out after {timeout_seconds:.1f} seconds"
        finally:
            if process.returncode is None:
                try:
                    process.terminate()
                except ProcessLookupError:
                    pass
                try:
                    await asyncio.wait_for(process.wait(), timeout=5)
                except asyncio.TimeoutError:
                    try:
                        process.kill()
                    except ProcessLookupError:
                        pass
                    await process.wait()

        transport: dict[str, Any]
        if failure_detail is not None:
            transport = {"Accepted": False, "TimedOut": True}
        elif process.returncode != 0:
            stderr_detail = stderr.decode(errors="replace")[-8000:]
            stdout_detail = stdout.decode(errors="replace")[-8000:]
            failure_detail = (
                f"UDPPCM client failed ({process.returncode}); "
                f"stdout: {stdout_detail}; stderr: {stderr_detail}"
            )
            try:
                transport = json.loads(stdout.decode(errors="strict"))
            except (UnicodeDecodeError, json.JSONDecodeError):
                transport = {
                    "Accepted": False,
                    "StdoutTail": stdout_detail,
                    "StderrTail": stderr_detail,
                }
        else:
            try:
                transport = json.loads(stdout.decode(errors="strict"))
            except (UnicodeDecodeError, json.JSONDecodeError) as error:
                failure_detail = f"UDPPCM client emitted no valid result: {error}"
                transport = {
                    "Accepted": False,
                    "StdoutTail": stdout.decode(errors="replace")[-8000:],
                }

        await asyncio.sleep(6)
        after = counters()
        delta = {name: after[name] - before[name] for name in COUNTER_NAMES}
        await wait_for(
            lambda: value("Joydex Audio Connected") is False
            and value("Joydex Audio Session Active") is False,
            "the UDPPCM session to close",
        )

        rearm_error: str | None = None
        try:
            await set_session("Armed")
            await wait_for(
                lambda: value("Joydex Wake Engine Running") is True,
                "wake inference to rearm",
                20,
            )
        except Exception as error:
            rearm_error = str(error)

        gauges = max_gauges()
        duration_gate_met = args.seconds >= QUALIFICATION_SECONDS
        result = {
            "scope": "adoption-matrix" if duration_gate_met else "smoke",
            "transport_gates_passed": False,
            "duration_gate_met": duration_gate_met,
            "adoption_matrix_case_passed": False,
            "device": {
                "name": info.name,
                "mac": info.mac_address,
                "project": info.project_name,
                "version": info.project_version,
            },
            "direction": args.direction,
            "seconds": args.seconds,
            "barge_in": barge_in,
            "transport": transport,
            "counter_delta": delta,
            "max_gauges": gauges,
            "max_gauge_limits": MAX_GAUGE_LIMITS,
            "socket_telemetry": {name: value(name) for name in TELEMETRY_NAMES},
            "final_state": value("Joydex Voice Session State"),
            "wake_rearmed": value("Joydex Wake Engine Running"),
            "rearm_error": rearm_error,
            "manual_checks": [
                "The xylophone ready cue played once before the transport marker.",
                "The low-level speaker marker was audible in speaker-only or duplex mode.",
                "No first syllable was clipped in a later natural-conversation trial.",
            ],
        }
        if failure_detail is not None or rearm_error is not None:
            detail = failure_detail or f"Voice PE failed to rearm: {rearm_error}"
            raise RuntimeError(
                f"{detail}\nDevice diagnostic snapshot:\n{json.dumps(result, indent=2)}"
            )

        expected_speaker_frames = args.seconds * 100 if args.direction != "microphone-only" else 0
        expected_speaker_bytes = expected_speaker_frames * 960
        if not transport.get("Accepted", False):
            raise RuntimeError(f"Host transport gates failed:\n{json.dumps(result, indent=2)}")
        if delta["Joydex Audio UDP Sessions"] != 1:
            raise RuntimeError(f"Unexpected UDP session count:\n{json.dumps(result, indent=2)}")
        if delta["Joydex Audio Downlink Frames"] != expected_speaker_frames:
            raise RuntimeError(f"Device downlink frame mismatch:\n{json.dumps(result, indent=2)}")
        if delta["Joydex Audio Speaker Accepted Bytes"] != expected_speaker_bytes:
            raise RuntimeError(f"Device speaker byte mismatch:\n{json.dumps(result, indent=2)}")

        zero_counters = (
            "Joydex Audio Uplink Dropped Bytes",
            "Joydex Audio Downlink Dropped Bytes",
            "Joydex Audio Downlink Underruns",
            "Joydex Audio Speaker Backpressure Events",
            "Joydex Audio Speaker Partial Writes",
            "Joydex Audio UDP Uplink Send Drops",
            "Joydex Audio UDP Invalid Packets",
            "Joydex Audio UDP Foreign Packets",
            "Joydex Audio UDP Downlink Missing Packets",
            "Joydex Audio UDP Downlink Late Packets",
            "Joydex Audio UDP Timeouts",
        )
        nonzero = {name: delta[name] for name in zero_counters if delta[name] != 0}
        if nonzero:
            raise RuntimeError(
                f"UDPPCM device error counters changed: {nonzero}\n{json.dumps(result, indent=2)}"
            )
        gauge_violations = {
            name: {"actual": gauges[name], "limit": limit}
            for name, limit in MAX_GAUGE_LIMITS.items()
            if gauges[name] > limit
        }
        if gauge_violations:
            raise RuntimeError(
                "UDPPCM queue or scheduler headroom gate failed: "
                f"{gauge_violations}\n{json.dumps(result, indent=2)}"
            )
        if args.direction != "speaker-only":
            minimum_uplink_frames = int(args.seconds * 50 * 0.9)
            if delta["Joydex Audio Uplink Frames"] < minimum_uplink_frames:
                raise RuntimeError(f"Insufficient microphone frames:\n{json.dumps(result, indent=2)}")
        if value("Joydex Audio Last Close Reason") != "host_close":
            raise RuntimeError(f"Unexpected close reason:\n{json.dumps(result, indent=2)}")
        if not result["wake_rearmed"] or result["final_state"] != "Armed":
            raise RuntimeError(f"Voice PE did not rearm:\n{json.dumps(result, indent=2)}")

        result["transport_gates_passed"] = True
        result["adoption_matrix_case_passed"] = duration_gate_met
        return result
    finally:
        if process is not None and process.returncode is None:
            try:
                process.kill()
            except ProcessLookupError:
                pass
            await process.wait()
        try:
            session = entities_by_name.get("Joydex Voice Session State")
            barge = entities_by_name.get("Joydex Audio Barge In")
            if session is not None:
                client.select_command(session.key, "Armed")
            if barge is not None:
                client.switch_command(barge.key, True)
            await asyncio.sleep(0.5)
        except Exception:
            pass
        await client.disconnect(force=True)


async def main() -> None:
    args = parse_args()
    result = await run(args)
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    asyncio.run(main())
