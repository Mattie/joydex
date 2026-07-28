# ESP32-4848S040C_I device reference

This document records reusable findings from bringing up Joydex on one
ESP32-4848S040C_I panel. Treat it as evidence for the tested unit, not a
promise that every similarly named seller listing has identical hardware.

Last updated: July 27, 2026.

## Confirmed identity

| Property | Tested finding | Confidence for another unit |
| --- | --- | --- |
| Purchased model | `ESP32-4848S040C_I`, sold as a 4-inch capacitive-touch panel | Medium; listings change |
| MCU | ESP32-S3 | High |
| Flash | 16 MiB | High |
| PSRAM | 8 MiB octal/OPI | High |
| Display | 4-inch 480×480 IPS TFT, ST7701-family RGB interface | High |
| Touch | Capacitive, using the GT911 ESPHome recipe | High for this recipe |
| USB | CH340 USB-to-UART bridge | High on the tested unit |
| Wi-Fi | Normal 2.4 GHz LAN | High |

The tested unit's rear silkscreen, optional relay population, and exact PCB
revision were not visible. Photograph and compare the rear of a new production
run if its behavior differs.

Primary references:

- [Tested AliExpress listing](https://www.aliexpress.us/item/3256808028364930.html)
  (no affiliation).
- [GUITION specification](https://www.guition.com/ku/icms/upload/fb081940d6fc11f09850077a33e1404f/FTPData/UEditor/file/2026121/1768961092477/ESP32-4848S040%20Specifications-EN.pdf).
- [ESPHome exact-board recipe](https://devices.esphome.io/devices/guition-esp32-s3-4848s040/).
- [ESPHome MIPI RGB component](https://esphome.io/components/display/mipi_rgb/).

## Display settings

Joydex pins ESPHome 2026.7.2 and uses the integrated
`GUITION-4848S040` model with explicit `C_I` timing:

```yaml
display:
  - platform: mipi_rgb
    model: GUITION-4848S040
    spi_mode: MODE3
    hsync_pulse_width: 8
    hsync_front_porch: 10
    hsync_back_porch: 20
    vsync_pulse_width: 8
    vsync_front_porch: 10
    vsync_back_porch: 10
```

Keep these overrides together until a newer ESPHome preset is physically
confirmed on the same panel revision. Compilation alone cannot prove
orientation, pixel alignment, color order, or clean blanking.

Relevant upstream reports:

- [ESPHome issue #13569](https://github.com/esphome/esphome/issues/13569)
- [ESPHome issue #17810](https://github.com/esphome/esphome/issues/17810)

The display uses a framebuffer in octal PSRAM. The current configuration uses
ESP-IDF, 8 MiB octal PSRAM at 80 MHz, a 12 MHz pixel clock, and RGB565.

For a dark LVGL skin, set the page itself to an opaque dark background.
Styling only the bottom layer left white gaps on the physical panel:

```yaml
pages:
  - id: main_page
    bg_color: 0x000000
    bg_opa: COVER
```

## Confirmed pin map

| Function | GPIO |
| --- | --- |
| Backlight PWM | 38 |
| Display initialization clock | 48 |
| Display initialization MOSI | 47 |
| Display chip select | 39 |
| RGB data enable | 18 |
| RGB horizontal sync | 16 |
| RGB vertical sync | 17 |
| RGB pixel clock | 21 |
| Red data | 11, 12, 13, 14, 0 |
| Green data | 8, 20, 3, 46, 9, 10 |
| Blue data | 4, 5, 6, 7, 15 |
| GT911 I2C SDA | 19 |
| GT911 I2C SCL | 45 |

The current backlight uses LEDC at 150 Hz with `ALWAYS_ON` restore mode.

### Pin hazards

- GPIO19 and GPIO20 are normally associated with ESP32-S3 native USB. This
  board uses them for touch and RGB display data, so ESPHome's native-USB
  warnings are expected.
- GPIO45 is a strapping pin used for GT911 SCL by the known board recipe.
- GPIO0 is boot-sensitive and belongs to the RGB bus.
- GPIO33 through GPIO37 belong to the octal flash/PSRAM configuration.
- GPIO47 and GPIO48 may also appear in vendor microSD examples.
- Vendor material assigns GPIO1, GPIO2, and GPIO40 to either audio or relay
  options depending on board population.
- The 16-bit RGB bus consumes most GPIOs. Treat any pin outside the confirmed
  table as unavailable until the exact PCB and optional components are known.

## USB, logging, and first flash

The tested panel entered the ESP32-S3 bootloader through its CH340 bridge
without a manual button sequence. A complete 16 MiB flash read, factory-image
write, verification, and hard reset succeeded through that path.

Re-discover the serial port before every operation. A Windows COM assignment
is temporary and is not a reliable identity when several identical panels are
connected.

ESPHome logs through UART0 at 115200 baud. GPIO19/GPIO20 warnings concern the
native USB path and do not prevent CH340 UART flashing.

Before the first write to each physical unit:

1. Connect only the intended panel.
2. Record its chip identity and flash size.
3. Read the complete 16 MiB flash into private storage.
4. Hash the backup and bind that record to the physical unit.
5. Validate and compile the chosen Joydex YAML.
6. Hash the exact generated factory image.
7. Reconfirm the port, unit, backup, and intended image before writing.

Example backup command:

```powershell
.\firmware\esphome\.venv\Scripts\python.exe -m esptool `
  --chip esp32s3 `
  --port <VERIFIED_COM_PORT> `
  --baud 460800 `
  read-flash 0x0 0x1000000 `
  <PRIVATE_UNIT_BACKUP>
```

Example first-flash command:

```powershell
.\firmware\esphome\.venv\Scripts\python.exe -m esptool `
  --chip esp32s3 `
  --port <VERIFIED_COM_PORT> `
  --baud 460800 `
  --after hard-reset `
  write-flash 0x0 <PRIVATE_FACTORY_IMAGE>
```

Whole-flash images can contain device data. Never publish them and never
restore one unit's backup to another unit.

## Wi-Fi findings

The panel runs on the normal wider 2.4 GHz LAN. The firmware has no captive
portal, fallback access point, Home Assistant dependency, or MQTT broker.
Wi-Fi power saving is disabled for responsive touch use.

Windows `.local` name resolution varied across tools during the canary. Direct
authenticated requests to the DHCP address worked consistently. For routine
use, reserve the panel's DHCP lease and configure Joydex with either the proven
hostname or that reserved address.

Do not publish the panel's LAN address, MAC address, SSID, or authentication
material in documentation or logs.

## REST and SSE behavior

The ESPHome Web Server supplies:

```text
GET  /events
POST /select/Task%201%20State/set?option=RUNNING
```

The stream sends catch-up state without a separate catch-up marker. Joydex
therefore suppresses the first state seen for each expected touch entity and
acts only on later `OFF` to `ON` transitions.

Normal state publications contain only changed task slots. Reconnects force a
complete four-slot replacement. This combination reduced redraw artifacts
without sacrificing recovery after a missed update.

All requests use Digest authentication. Digest protects the password
challenge/response, but the HTTP traffic has no TLS confidentiality or server
identity. Keep the panel on a trusted LAN and never port-forward it.

## Current screen contract

The public firmware contains two compatible skins:

- `joydex-panel.yaml`: neutral white baseline.
- `joydex-panel-bridge.yaml`: dark bridge-console skin.

Both expose four task controls and one visible PLAN MODE control:

| Joydex meaning | ESPHome state | Card styling |
| --- | --- | --- |
| Empty | `EMPTY` | Blank gray |
| Running | `RUNNING` | White, gray border and text |
| Approval or attention | `ATTENTION` | Yellow, gray border and text |
| Complete | `COMPLETE` | Green, white text |
| Fault | `ATTENTION` | Same treatment as attention |

The visible PLAN MODE control retains the ESPHome entity name `Sidebar` and
internal ID `sidebar_pressed` for compatibility. Joydex's host model and action
remain Plan Mode.

Pressed feedback is local to LVGL: the touched control contracts slightly and
gains a cyan border until release. The screen has no persistent action-result
line.

## Redraw and flicker findings

The panel can show brief tearing when a state change causes multiple large
regions to redraw. The current implementation limits this in two places:

- static chrome is created once and does not change;
- Joydex sends only changed task selects during ordinary operation.

Press feedback alone should not trigger a full-screen refresh. A reconnect may
refresh all four cards because Joydex cannot assume the panel retained every
prior update.

Experimental acceptance:

- no incorrect task activation;
- no white full-screen flash;
- no sustained tearing;
- any remaining artifact is brief and confined to the changed task card.

## OTA and recovery

Available:

- password-protected native ESPHome OTA;
- two generated OTA app slots;
- ESP-IDF bootloader rollback support;
- CH340 UART access;
- a private, unit-specific factory backup.

Physically confirmed on the tested unit:

- repeated OTA uploads;
- reboot and LAN rejoin;
- SSE reconnection;
- complete task-state convergence after reconnect.

Not yet claimed:

- full factory restoration and factory-firmware boot;
- deliberately interrupted OTA recovery;
- bad-image rollback under power loss;
- Secure Boot or flash encryption.

Keep irreversible eFuse changes out of this experiment. Prove the private
factory restore and failure recovery on a spare panel before relying on them.

## Repeatability checklist

1. Verify the exact seller model, display resolution, and capacitive-touch
   option.
2. Photograph the front, rear, module markings, and connector population.
3. Test the factory display, orientation, brightness, and touch.
4. Verify stable 5 V USB power and a data-capable cable.
5. Probe the ESP32-S3, flash, PSRAM, and bridge identity.
6. Capture and hash that unit's complete factory flash.
7. Provision unique Wi-Fi, Digest, and OTA secrets in ignored local storage.
8. Validate and compile with the pinned ESPHome version.
9. Hash and flash the exact factory artifact.
10. Verify display alignment, color, every touch target, and pressed feedback.
11. Verify trusted-LAN join and reserve the DHCP lease.
12. Test authenticated REST state changes and the SSE stream.
13. Cold power-cycle and verify host/panel convergence.
14. Test the matching OTA image.
15. Record results privately without credentials or network identifiers.

## Open hardware questions

- PCB revision and relay population across seller batches.
- Long-duration display, Wi-Fi, and power stability.
- Full private factory restoration.
- Interrupted-update behavior.
- microSD, relay, audio, and expansion-pin behavior on the exact hidden PCB
  revision.
- Peak current at full brightness during Wi-Fi activity.

Update this document with reusable physical evidence. Keep credentials,
addresses, MACs, whole-flash images, unit identities, and compiled artifacts
in private storage.
