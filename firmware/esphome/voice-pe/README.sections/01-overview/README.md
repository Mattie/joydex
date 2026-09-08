# Joydex Voice PE bridge canary

This folder prepares a source-only firmware derivative for the Home Assistant Voice Preview Edition. It does not discover, connect to, or upload to a device.

The canary is pinned to:

- official Voice PE commit `a163e7b980c572df9d3811ff98094350f5aae541` (tag `25.12.4`);
- ESPHome `2025.12.2`, matching the local diagnostics snapshot;
- Espressif `esp-nn` `1.1.2`, matching the tensor arenas in the pinned 2025 microWakeWord manifests;
- micro-wake-word manifests, including experimental `Okay Computer`, at repository commit `05b65922cc433c9df13e98e32a7fe520758c837e`.

The patch also replaces the official base's `dev` voice-kit reference and `raw/dev` sound URLs with the exact `25.12.4` commit so a later build cannot silently mix newer assets into the canary.

The patch makes the wake route exclusive. It replaces every `voice_assistant.start` action in the pinned base, adds `Okay Computer` at the pinned model revision, preserves the mute check and wake sound, and publishes a momentary `Joydex Voice Wake` binary sensor. The stock models remain compiled because the official timer and sensitivity code references them. The overlay disables `Okay Nabu`, `Hey Jarvis`, `Hey Mycroft`, and `Stop` before starting microWakeWord, leaving only `Okay Computer` plus VAD eligible for runtime tensor allocation. Every compiled wake model follows the Joydex-only callback, so one wake cannot start both Home Assistant Assist and Joydex.

The overlay adds ESPHome Web Server SSE on the trusted LAN without credentials. Joydex will connect outward to `GET /events`, seed the first observed state without dispatching it, and act only on a later `OFF -> ON` edge. No inbound Windows listener or firewall rule is needed for that control plane. If station Wi-Fi fails, a passworded `Joydex Voice PE Recovery` fallback access point and captive portal keep later recovery wireless; its password reuses the local ignored `web_server_password` secret.

The overlay also starts microWakeWord during late boot. The official base normally starts it when a Home Assistant Voice API client connects; the dedicated Joydex wake path therefore remains available when Home Assistant is offline or removed.

Version `0.1.8` adds device-owned, persisted number entities for microphone gain, wake probability cutoff, wake sliding-window size, and VAD probability cutoff. Joydex loads the current values before enabling its **Apply to device** action, writes them through ESPHome's LAN REST API, and reads all four back for confirmation. The firmware defaults remain the accepted `0.1.6` profile: gain `4`, wake cutoff `0.35`, window `5`, and VAD cutoff `0.10`.

Version `0.1.9` is a separate score-diagnostic image derived from the exact `0.1.6` behavior. It keeps those four accepted values fixed and adds only classifier telemetry: high-water wake average/maximum, VAD average/maximum, a reset generation, and a LAN-accessible **Joydex Reset Score Peaks** button. Wake scores continue accumulating when VAD would block detection, so the trial can distinguish a VAD miss from a weak wake-model match. The telemetry retains quantized probabilities only; it stores no microphone audio.

Version `0.1.10` was an offline MP3 sound canary layered on that exact `0.1.9` diagnostic. Wake detection still reached Joydex, but the device produced no acknowledgement sound, so the image failed its runtime acceptance check despite passing configuration, generated-code, and compile checks.

Version `0.1.11` replaces that payload with `sounds/WakeBlip.flac` in the stock-supported 48 kHz, mono, 16-bit format. It also exposes **Joydex Voice Session State** with `Armed`, `Starting`, `Listening`, and `Error` options. A detected wake immediately selects `Starting`; the running Joydex app selects `Listening` only after its allowlisted Codex realtime-start marker and returns the endpoint to `Armed` after the matching stop marker. A 20-second start timeout shows a short error state and rearms the endpoint.

ESPHome `2025.12.2` already provides runtime setters for gain and probability cutoff. Its sliding-window vector has no runtime setter, so `prepare-canary.ps1` also checks out ESPHome commit `99f7e9aeb74447fa489fefb80b8129623e916816`, applies the reviewed `micro-wake-window-runtime.patch`, and verifies both changed headers by SHA-256. The window script stops microWakeWord and waits for the inference task to finish before resizing the vector, then restarts detection. The stock sensitivity selector is internal because it controls only the disabled stock wake models.

