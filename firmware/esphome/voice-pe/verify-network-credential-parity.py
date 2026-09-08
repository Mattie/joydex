"""Verify compiled ESPHome station credentials without printing their values."""

from __future__ import annotations

import argparse
import ast
import hmac
import re
import sys
from pathlib import Path

import yaml


WIFI_AP_PATTERN = re.compile(
    r"wifi::WiFiAP\s+(?P<variable>[A-Za-z_][A-Za-z0-9_]*)\s*=.*?"
    r"(?P=variable)\.set_ssid\((?P<ssid>\"(?:\\.|[^\"\\])*\")\);.*?"
    r"(?P=variable)\.set_password\((?P<password>\"(?:\\.|[^\"\\])*\")\);",
    re.DOTALL,
)


def parse_args() -> argparse.Namespace:
    """Return the two local files used for source-to-build parity."""
    parser = argparse.ArgumentParser()
    parser.add_argument("--secrets", required=True, type=Path)
    parser.add_argument("--generated-main", required=True, type=Path)
    return parser.parse_args()


def decode_cpp_string(literal: str) -> str:
    """Decode the C++ string form ESPHome emits for a YAML scalar."""
    value = ast.literal_eval(literal)
    if not isinstance(value, str):
        raise ValueError("Generated Wi-Fi credential is not a string literal.")
    return value


def main() -> int:
    """Compare source secrets with the one generated station network."""
    args = parse_args()
    secrets = yaml.safe_load(args.secrets.read_text(encoding="utf-8"))
    if not isinstance(secrets, dict):
        raise ValueError("The ESPHome secrets file is not a YAML mapping.")

    source_ssid = secrets.get("wifi_ssid")
    source_password = secrets.get("wifi_password")
    recovery_password = secrets.get("web_server_password")
    if not all(
        isinstance(value, str)
        for value in (source_ssid, source_password, recovery_password)
    ):
        raise ValueError("The ESPHome station or recovery source values are not strings.")

    generated = args.generated_main.read_text(encoding="utf-8")
    access_points = {
        match.group("variable"): (
            decode_cpp_string(match.group("ssid")),
            decode_cpp_string(match.group("password")),
        )
        for match in WIFI_AP_PATTERN.finditer(generated)
    }
    station_variables = re.findall(r"add_sta\(([A-Za-z_][A-Za-z0-9_]*)\);", generated)
    recovery_variables = re.findall(r"set_ap\(([A-Za-z_][A-Za-z0-9_]*)\);", generated)
    if len(station_variables) != 1 or station_variables[0] not in access_points:
        raise ValueError("Expected exactly one generated station network.")
    if len(recovery_variables) != 1 or recovery_variables[0] not in access_points:
        raise ValueError("Expected exactly one generated recovery network.")

    compiled_ssid, compiled_password = access_points[station_variables[0]]
    if not hmac.compare_digest(source_ssid, compiled_ssid) or not hmac.compare_digest(
        source_password, compiled_password
    ):
        raise ValueError("Compiled station credentials differ from the selected source.")

    recovery_ssid, compiled_recovery_password = access_points[recovery_variables[0]]
    if recovery_ssid != "Joydex Voice PE Recovery" or not hmac.compare_digest(
        recovery_password, compiled_recovery_password
    ):
        raise ValueError("Compiled recovery credentials differ from the selected source.")

    print("NETWORK CREDENTIAL PARITY: PASS")
    print("RECOVERY CREDENTIAL PARITY: PASS")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception:
        print("NETWORK CREDENTIAL PARITY: FAIL", file=sys.stderr)
        raise SystemExit(1) from None
