#!/usr/bin/env python3
"""Run Voice PE downlink-control, uplink-only, and full-duplex physical canaries."""

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
    "Joydex Audio WebSocket Send Failures",
    "Joydex Audio WebSocket Send Stalls",
    "Joydex Audio WebSocket Receive Failures",
    "Joydex Audio WebSocket Forced Closes",
    "Joydex Audio WebSocket Connections",
)

MAX_GAUGE_NAMES = (
    "Joydex Audio Uplink Queue High Water Bytes",
    "Joydex Audio Downlink Queue High Water Bytes",
    "Joydex Audio Downlink Max Loop Gap Milliseconds",
    "Joydex Audio WebSocket Send Max Duration Milliseconds",
)

TELEMETRY_NAMES = (
    "Joydex Audio Last Socket Errno",
    "Joydex Audio Last Close Reason",
)

CONTROL_NAMES = (
    "Joydex Voice Session State",
    "Joydex Audio Barge In",
    "Joydex Wake Engine Running",
    "Joydex Microphone Capturing",
)


def parse_args() -> argparse.Namespace:
    repository = Path(__file__).resolve().parents[2]
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", required=True)
    parser.add_argument("--expected-name", required=True)
    parser.add_argument("--expected-mac", required=True)
    parser.add_argument("--expected-version", required=True)
    parser.add_argument("--frames", type=int, default=200)
    parser.add_argument("--inject-recovery", action="store_true")
    parser.add_argument("--uplink-only", action="store_true")
    parser.add_argument(
        "--signal-profile",
        choices=("tone", "quiet-markers", "silence"),
        default="tone",
    )
    parser.add_argument(
        "--dotnet",
        type=Path,
        default=repository / ".tools" / "dotnet" / "dotnet.exe",
    )
    parser.add_argument(
        "--project",
        type=Path,
        default=Path(__file__).resolve().with_name("Joydex.VoicePeAudioCanary.csproj"),
    )
    args = parser.parse_args()
    if args.frames <= 0:
        parser.error("--frames must be positive")
    args.assembly = (
        args.project.parent
        / "bin"
        / "Release"
        / "net8.0-windows"
        / "Joydex.VoicePeAudioCanary.dll"
    )
    if not args.assembly.is_file():
        parser.error(f"Build the PCM canary first; assembly is missing: {args.assembly}")
    return args


async def wait_for(
    predicate: Callable[[], bool], description: str, timeout_seconds: float = 12
) -> None:
    deadline = asyncio.get_running_loop().time() + timeout_seconds
    while asyncio.get_running_loop().time() < deadline:
        if predicate():
            return
        await asyncio.sleep(0.2)
    raise RuntimeError(f"Timed out waiting for {description}")


def require_zero(result: dict[str, Any], name: str) -> None:
    if result[name] != 0:
        raise RuntimeError(f"{name} increased by {result[name]}; expected zero")


async def run(args: argparse.Namespace) -> dict[str, Any]:
    expected_mac = args.expected_mac.replace(":", "").lower()
    client = APIClient(
        args.host,
        6053,
        None,
        client_info="joydex-duplex-canary",
        expected_name=args.expected_name,
        expected_mac=expected_mac,
    )
    await client.connect(login=True)
    entities_by_name: dict[str, Any] = {}
    states: dict[int, Any] = {}
    try:
        info, entities, _ = await client.device_info_and_list_entities()
        actual_mac = (info.mac_address or "").replace(":", "").lower()
        if info.name != args.expected_name or actual_mac != expected_mac:
            raise RuntimeError("Voice PE identity does not match the required name and MAC")
        if info.project_name != "Joydex.Voice PE Audio Bridge":
            raise RuntimeError(f"Unexpected project: {info.project_name}")
        if info.project_version != args.expected_version:
            raise RuntimeError(f"Unexpected firmware version: {info.project_version}")

        entities_by_name = {entity.name: entity for entity in entities}
        missing = [
            name
            for name in (*COUNTER_NAMES, *MAX_GAUGE_NAMES, *TELEMETRY_NAMES, *CONTROL_NAMES)
            if name not in entities_by_name
        ]
        if missing:
            raise RuntimeError(f"Required diagnostic entities are missing: {missing}")

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
            client.select_command(
                entities_by_name["Joydex Voice Session State"].key, target
            )
            await wait_for(
                lambda: value("Joydex Voice Session State") == target,
                f"session={target}",
            )

        def counters() -> dict[str, int]:
            return {
                name: int(round(value(name) or 0)) for name in COUNTER_NAMES
            }

        def max_gauges() -> dict[str, int]:
            return {
                name: int(round(value(name) or 0)) for name in MAX_GAUGE_NAMES
            }

        async def run_case(
            label: str,
            barge_in: bool,
            inject_recovery: bool = False,
            transport_mode: str = "duplex",
        ) -> dict[str, Any]:
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
            await asyncio.sleep(5.5)
            before = counters()

            process = await asyncio.create_subprocess_exec(
                str(args.dotnet),
                str(args.assembly),
                f"http://{args.host}",
                str(args.frames),
                str(args.frames // 2 if inject_recovery else -1),
                args.signal_profile,
                transport_mode,
                stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.PIPE,
            )
            # Match the production lifecycle once the media process is running. Leaving the
            # endpoint in Starting trips its bounded startup timeout during long canaries.
            await set_session("Listening")
            timeout = max(45, args.frames * 0.020 + 30)
            stdout = b""
            stderr = b""
            failure_detail: str | None = None
            try:
                stdout, stderr = await asyncio.wait_for(process.communicate(), timeout=timeout)
            except asyncio.TimeoutError:
                failure_detail = f"PCM canary timed out after {timeout} seconds"
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
            lines = [line for line in stdout.decode(errors="replace").splitlines() if line]
            if failure_detail is not None:
                transport = {
                    "completed": False,
                    "returnCode": process.returncode,
                    "timedOut": True,
                }
            elif process.returncode != 0:
                detail = stderr.decode(errors="replace")[-8000:]
                failure_detail = f"PCM canary failed ({process.returncode}): {detail}"
                transport = {
                    "completed": False,
                    "returnCode": process.returncode,
                    "stderrTail": detail,
                }
            else:
                try:
                    transport = json.loads(lines[-1])
                except (IndexError, json.JSONDecodeError) as error:
                    failure_detail = f"PCM canary emitted no valid result: {error}"
                    transport = {
                        "completed": False,
                        "returnCode": process.returncode,
                        "stdoutTail": "\n".join(lines)[-8000:],
                    }

            await asyncio.sleep(6)
            after = counters()
            delta = {name: after[name] - before[name] for name in COUNTER_NAMES}
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

            result = {
                "label": label,
                "barge_in": barge_in,
                "inject_recovery": inject_recovery,
                "transport_mode": transport_mode,
                "transport": transport,
                "counter_delta": delta,
                "max_gauges": max_gauges(),
                "socket_telemetry": {
                    name: value(name) for name in TELEMETRY_NAMES
                },
                "final_state": value("Joydex Voice Session State"),
                "wake_rearmed": value("Joydex Wake Engine Running"),
                "rearm_error": rearm_error,
            }
            if failure_detail is not None:
                raise RuntimeError(
                    f"{failure_detail}\nDevice diagnostic snapshot:\n"
                    f"{json.dumps(result, indent=2)}"
                )
            if rearm_error is not None:
                raise RuntimeError(
                    "Voice PE failed to rearm after the canary.\n"
                    f"Device diagnostic snapshot:\n{json.dumps(result, indent=2)}"
                )
            return result

        if args.uplink_only:
            uplink = await run_case(
                "uplink_only",
                True,
                transport_mode="uplink-only",
            )
            await set_barge_in(True)
            delta = uplink["counter_delta"]
            if uplink["transport"]["concurrentMicrophoneFrames"] < max(1, int(args.frames * 0.75)):
                raise RuntimeError("Uplink-only canary did not carry sustained microphone audio")
            if delta["Joydex Audio WebSocket Connections"] != 1:
                raise RuntimeError("Uplink-only canary WebSocket connection count was unexpected")
            for counter in (
                "Joydex Audio Uplink Dropped Bytes",
                "Joydex Audio Downlink Frames",
                "Joydex Audio Downlink Dropped Bytes",
                "Joydex Audio Speaker Backpressure Events",
                "Joydex Audio WebSocket Send Failures",
                "Joydex Audio WebSocket Send Stalls",
                "Joydex Audio WebSocket Receive Failures",
                "Joydex Audio WebSocket Forced Closes",
            ):
                require_zero(delta, counter)
            if not uplink["wake_rearmed"] or uplink["final_state"] != "Armed":
                raise RuntimeError("Voice PE did not rearm after the uplink-only canary")
            return {
                "accepted": True,
                "device": {
                    "name": info.name,
                    "mac": info.mac_address,
                    "project": info.project_name,
                    "version": info.project_version,
                },
                "results": [uplink],
                "restored": {
                    "session": value("Joydex Voice Session State"),
                    "barge_in": value("Joydex Audio Barge In"),
                    "wake_engine": value("Joydex Wake Engine Running"),
                    "microphone_capturing": value("Joydex Microphone Capturing"),
                },
            }

        control = await run_case("control_off", False)
        await asyncio.sleep(1)
        duplex = await run_case("duplex_on", True, args.inject_recovery)
        await set_barge_in(True)

        expected_bytes = args.frames * 960
        for case in (control, duplex):
            delta = case["counter_delta"]
            maximum_frames = args.frames + (1 if case["inject_recovery"] else 0)
            if not args.frames <= delta["Joydex Audio Downlink Frames"] <= maximum_frames:
                raise RuntimeError("Device downlink-frame count did not match the canary")
            minimum_accepted_bytes = (
                int(expected_bytes * 0.9)
                if case["inject_recovery"]
                else expected_bytes
            )
            if not minimum_accepted_bytes <= delta["Joydex Audio Speaker Accepted Bytes"] <= maximum_frames * 960:
                raise RuntimeError("Device did not accept the complete speaker payload")
            expected_connections = 3 if case["inject_recovery"] else 1
            if delta["Joydex Audio WebSocket Connections"] != expected_connections:
                raise RuntimeError("Audio canary WebSocket connection count was unexpected")
            for counter in (
                "Joydex Audio Uplink Dropped Bytes",
                "Joydex Audio Downlink Dropped Bytes",
                "Joydex Audio Speaker Backpressure Events",
                "Joydex Audio Speaker Partial Writes",
            ):
                require_zero(delta, counter)
            if not case["inject_recovery"]:
                for counter in (
                    "Joydex Audio WebSocket Send Failures",
                    "Joydex Audio WebSocket Send Stalls",
                    "Joydex Audio WebSocket Receive Failures",
                    "Joydex Audio WebSocket Forced Closes",
                ):
                    require_zero(delta, counter)
            elif not case["transport"]["reconnected"]:
                raise RuntimeError("Recovery canary did not observe a host reconnect")
            if not case["wake_rearmed"] or case["final_state"] != "Armed":
                raise RuntimeError("Voice PE did not rearm after the canary")

        control_microphone_frames = control["transport"]["concurrentMicrophoneFrames"]
        if control_microphone_frames > 5:
            raise RuntimeError(
                "Barge-in-off control carried more than 100 ms of microphone audio "
                f"during playback ({control_microphone_frames} frames)"
            )
        minimum_duplex_frames = max(1, int(args.frames * 0.75))
        if duplex["transport"]["concurrentMicrophoneFrames"] < minimum_duplex_frames:
            raise RuntimeError("Full-duplex canary did not carry sustained microphone audio")

        return {
            "accepted": True,
            "device": {
                "name": info.name,
                "mac": info.mac_address,
                "project": info.project_name,
                "version": info.project_version,
            },
            "results": [control, duplex],
            "restored": {
                "session": value("Joydex Voice Session State"),
                "barge_in": value("Joydex Audio Barge In"),
                "wake_engine": value("Joydex Wake Engine Running"),
                "microphone_capturing": value("Joydex Microphone Capturing"),
            },
        }
    finally:
        try:
            session = entities_by_name.get("Joydex Voice Session State")
            barge_in = entities_by_name.get("Joydex Audio Barge In")
            if session is not None:
                client.select_command(session.key, "Armed")
            if barge_in is not None:
                client.switch_command(barge_in.key, True)
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
