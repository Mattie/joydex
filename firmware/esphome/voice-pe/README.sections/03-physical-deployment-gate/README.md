## Physical deployment gate

Stop before `upload`, the ESPHome dashboard install action, or any web-installer step. Before the first write, retain the matching official recovery image, verify stable power and physical access, confirm the exact device address, and compare the generated partition/flash settings with the 16 MB ESP32-S3 base. USB is the fallback recovery route and is not required for a successful native OTA.

The 2026-08-23 **WAKECANARY** preflight resolved `joydex-voice-pe.local` to `192.0.2.10`, reached the native API on TCP 6053 and native OTA on TCP 3232, and confirmed that HTTP port 80 is still closed on the stock image. The canary image reports ESP32-S3, 16 MB DIO flash, a valid checksum, and ESPHome `2025.12.2`; its Wi-Fi secrets are present and contain no preparation placeholders. Matching official `25.12.4` rollback assets are staged outside the repository at `%TEMP%\joydex-voice-pe-recovery-25.12.4` and verified against their published SHA-256 files:

| Official recovery asset | Bytes | SHA-256 |
|---|---:|---|
| `home-assistant-voice-esp32s3.ota.bin` | 3,372,032 | `41A6C97D30CBD1766F41A0DD47E464D0C2A491D9BA003A634294D1AC7410E1BA` |
| `home-assistant-voice-esp32s3.factory.bin` | 3,437,568 | `C01256452403993EF654963FCDDB19D5B4520D10A01C771151AB8919777D64CF` |

The first **OTAFLASH** transfer completed successfully, but the preparation path had copied `secrets.example.yaml` into the workspace as `secrets.yaml`. Its literal `REPLACE_WIFI_SSID` and `REPLACE_WIFI_PASSWORD` values compiled and uploaded, so the new image booted without rejoining the LAN. That derivative had no fallback access point or Improv BLE component and required the documented USB bootloader recovery path. The deployment gate now rejects example-file identity and placeholder values and requires an explicitly selected, known-working ESPHome secrets file.

The corrected build preserves the retail identity `joydex-voice-pe`, explicitly copies the known-working `firmware\esphome\secrets.yaml`, and remains under `.tools\voice-pe-canary-corrected`. It also adds a passworded `Joydex Voice PE Recovery` fallback access point and captive portal so a later station-Wi-Fi failure remains wirelessly recoverable. Its verified artifacts are:

| Corrected artifact | Bytes | SHA-256 |
|---|---:|---|
| `firmware.bin` / `firmware.ota.bin` | 3,118,800 | `F64F660B65C6C7A17CFDDFFA23561124BD505CB5766DC0776C6C7999F1BBEE5E` |
| `firmware.factory.bin` | 3,184,336 | `B09ABE9B49A8CB0CF71BB1D480BC862D942C5182A297AB8C4CB68DC68EB60BEC` |

The corrected image reports ESP32-S3, 16 MB DIO flash, ESPHome `2025.12.2`, a valid checksum and validation hash, the expected `Joydex Voice Wake` entity, and the recovery access point. On 2026-08-23 it was written over USB and read-back verification passed; the device rejoined as `joydex-voice-pe.local` / `192.0.2.10`, and its Joydex wake REST endpoint returned HTTP 200. That USB session reported MAC `02:00:00:00:00:02`, but subsequent diagnostics running on the custom endpoint report both the eFuse and Wi-Fi MAC as `02:00:00:00:00:01`. The earlier USB MAC is therefore not an accepted identifier for this Voice PE.
