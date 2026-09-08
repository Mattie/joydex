"""Verify the exact Voice PE target over its native API before an attended OTA."""

from __future__ import annotations

import argparse
import asyncio

from aioesphomeapi import APIClient


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", required=True)
    parser.add_argument("--name", required=True)
    parser.add_argument("--mac", required=True)
    parser.add_argument("--project", required=True)
    parser.add_argument("--version", required=True)
    return parser.parse_args()


async def verify(args: argparse.Namespace) -> None:
    expected_mac = args.mac.replace(":", "").lower()
    client = APIClient(
        args.host,
        6053,
        None,
        client_info="joydex-attended-ota-preflight",
        expected_name=args.name,
        expected_mac=expected_mac,
    )
    await client.connect(login=True)
    try:
        info = await client.device_info()
        actual_mac = (info.mac_address or "").replace(":", "").lower()
        if (
            info.name != args.name
            or actual_mac != expected_mac
            or info.project_name != args.project
            or info.project_version != args.version
        ):
            raise RuntimeError("The connected Voice PE identity does not match the OTA target.")
    finally:
        await client.disconnect(force=True)


def main() -> int:
    args = parse_args()
    asyncio.run(verify(args))
    print("VOICE PE OTA TARGET IDENTITY: PASS")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
