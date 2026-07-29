# Experimental Joydex ESPHome touchscreen

This directory contains source examples for the
ESP32-4848S040C_I / GUITION-4848S040 480×480 capacitive touchscreen. The
panel joins a normal 2.4 GHz Wi-Fi network and talks directly to the Joydex
Windows app. Home Assistant, MQTT, the ESPHome native API, a captive portal,
and a separate runtime access point are not required.

This is experimental support tested on one physical panel. Seller listings and
board revisions can change, so verify the exact model and pinout before
flashing:

- [Tested AliExpress listing](https://www.aliexpress.us/item/3256808028364930.html)
  (purchased as `ESP32-4848S040C_I`; no affiliation).
- [GUITION specification](https://www.guition.com/ku/icms/upload/fb081940d6fc11f09850077a33e1404f/FTPData/UEditor/file/2026121/1768961092477/ESP32-4848S040%20Specifications-EN.pdf).
- [Device-specific findings and recovery notes](../../docs/ESP32_4848S040C_I_DEVICE_REFERENCE.md).

## Choose a skin

- `joydex-panel.yaml` is the neutral white baseline.
- `joydex-panel-bridge.yaml` is the dark retro-futuristic bridge-console skin.

Both expose the same host contract:

- `Task 1 State` through `Task 4 State` accept `EMPTY`, `RUNNING`,
  `ATTENTION`, and `COMPLETE`.
- `Task 1` through `Task 4` are momentary touch controls.
- The visible `PLAN MODE` control retains the ESPHome entity name `Sidebar`
  for compatibility with the first deployed firmware.
- `/events` supplies authenticated Server-Sent Events to Joydex.

Empty task positions are blank gray. Running tasks are white with gray borders
and text. Attention tasks are yellow with gray borders and text. Completed
tasks are green with white text. Pressed controls contract slightly and gain a
cyan border until release.

## Prerequisites

- Python 3.12
- A data-capable USB cable for the first flash
- A trusted 2.4 GHz LAN
- A private backup of the panel's original flash
- Joydex built from this repository

ESPHome is pinned in `requirements.txt` so the example does not silently adopt
new display defaults.

## Prepare secrets

From `firmware/esphome`:

```powershell
Copy-Item -LiteralPath .\secrets.example.yaml -Destination .\secrets.yaml
```

Replace every placeholder in `secrets.yaml`. Use distinct, long random values
for the Web Server and OTA passwords. The real file is ignored by Git.

The generated `.esphome` directory can contain expanded secrets, and compiled
firmware contains the credentials it needs at runtime. Both remain private and
ignored.

## Install the pinned ESPHome CLI

```powershell
py.exe -3.12 -m venv .venv
.\.venv\Scripts\python.exe -m pip install --requirement .\requirements.txt
```

## Validate and compile

```powershell
.\.venv\Scripts\esphome.exe config .\joydex-panel.yaml
.\.venv\Scripts\esphome.exe config .\joydex-panel-bridge.yaml

.\.venv\Scripts\esphome.exe compile .\joydex-panel.yaml
.\.venv\Scripts\esphome.exe compile .\joydex-panel-bridge.yaml
```

Warnings about GPIO19 and GPIO20 being unavailable to native
USB-Serial-JTAG are expected on this board: those pins are used by touch and
RGB display data. The tested panel exposes a CH340 UART bridge for flashing and
logging.

## First flash

Before writing a panel:

1. Confirm the exact model and current serial port.
2. Read and hash a complete 16 MiB factory backup for that physical unit.
3. Validate and compile the chosen YAML.
4. Hash the exact generated factory image.
5. Keep every backup and compiled image outside the repository.

Use `esptool` against the verified port and exact factory image:

```powershell
.\.venv\Scripts\python.exe -m esptool `
  --chip esp32s3 `
  --port <VERIFIED_COM_PORT> `
  --baud 460800 `
  --after hard-reset `
  write-flash 0x0 <PRIVATE_FACTORY_IMAGE>
```

Do not restore one panel's whole-flash backup to another panel.

## Configure Joydex

From the repository root:

```powershell
dotnet run `
  --project .\tools\Joydex.WirelessPanel.Configure\Joydex.WirelessPanel.Configure.csproj
```

Enter the panel endpoint, Web Server username, and matching password. Start
with `http://joydex-panel.local/`. If `.local` resolution is unreliable on the
Windows host, reserve the panel's DHCP lease and use that stable LAN address.

The password is hidden while typed and saved with Windows CurrentUser DPAPI at:

```text
%LOCALAPPDATA%\Joydex\WirelessPanel\panel.json
```

Restart Joydex or choose **Reload configuration** after changing the panel
settings. Published Joydex builds include
`Joydex.WirelessPanel.Configure.exe`.

## Updates

After the initial USB flash, use password-protected ESPHome OTA with the
panel's hostname or reserved LAN address:

```powershell
.\.venv\Scripts\esphome.exe upload .\joydex-panel-bridge.yaml `
  --device <PANEL_HOST_OR_ADDRESS>
```

Keep the neutral skin and the unit-specific factory backup available as
rollback paths.

## Security boundary

ESPHome's Web Server uses Digest authentication and disables web-based OTA and
log handlers. Native password-protected ESPHome OTA remains enabled.

The REST and SSE traffic is still HTTP without TLS. Use the example only on a
trusted LAN:

- never port-forward the panel;
- do not expose it to the public internet;
- use unique Web Server and OTA passwords;
- reserve its DHCP lease if hostname resolution is unreliable;
- keep backups, build caches, and firmware binaries private.

## Transport behavior

Joydex opens authenticated `GET /events`. ESPHome sends current-state catch-up
events without a separate marker, so Joydex suppresses the first state
observed for each expected touch entity and then reacts to live `OFF` to `ON`
edges.

Normal task changes post only the slots whose projected state changed, which
limits display redraws. Every SSE reconnect forces a complete four-slot
replacement so the panel converges after a network or host interruption.

Example state update:

```text
POST /select/Task%201%20State/set?option=RUNNING
```

See the [research record](../../docs/WIRELESS_TOUCHSCREEN_RESEARCH_V1.1.md) for
the architecture decision and the physical findings behind this example.
