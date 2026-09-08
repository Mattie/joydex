# ADR 0004: Stage the modern Sendspin player behind the legacy media-source ABI

- Status: Accepted
- Date: 2026-08-29

## Context

ADR 0003 established a working host protocol and call-scoped stream, but the
pinned ESPHome Sendspin player still skipped scheduled chunks during sustained
or repeated playback. Host queues and model audio remained complete while
device late-chunk counters grew. Priority, Wi-Fi lease, power-saving, and delay
experiments did not produce a reliable legacy player.

`sendspin-cpp` `v0.7.2` retains the plaintext player-v1 wire contract Joydex
uses, including the hello, clock, stream, and timestamped-audio messages. Its
ESPHome adapter targets a newer single-pipeline `MediaSource` interface, while
the pinned Voice PE source exposes a pipeline-indexed interface. Replacing the
whole voice stack would discard already-working wake, Codex ownership,
microphone, cue, transcript, and hangup behavior; the useful replacement
boundary is only the device-side player and synchronizer.

## Decision

- Pin `sendspin-cpp` `v0.7.2` at commit
  `30514d5102c269a0c7fa6a13932d6bf7f2ae1abc`.
- Preserve Joydex's existing port `8765` microphone/control lane, port `8927`
  speaker lane, 48 kHz mono Opus format, 20 ms cadence, one-second lead, and one
  stream per Voice Session.
- Bridge modern `PlayerRoleListener` writes to pipeline `0` of the pinned
  ESPHome `MediaSource`; report actual speaker progress back through
  `PlayerRole::notify_audio_played`.
- Publish `SYNCHRONIZED` only after the speaker orchestrator installs pipeline
  `0` and resets pending-frame accounting. Atomic readiness and frame counters
  cross the Sendspin worker, mixer, speaker callback, and main loop.
- Carry only narrow playback-accounting backports from mixer commit
  `0fe230114c80b6d3378f04c777baadee178e40ad` and speaker-source commit
  `f5c1a8111df78e32d07d7a7bb800018808cdc0e7`; do not upgrade ESPHome wholesale.
- Enable only the player and controller roles. Metadata, artwork, visualizer,
  and color roles stay outside this endpoint.
- Interpret `buffer_capacity` as compressed bytes. Joydex requires the modern
  adapter's 20,000-byte advertised minimum independently of timestamp lead.
- Run the modern player's 6,192-byte worker stack in internal RAM. Large media
  allocations and the independent HTTP worker may remain in PSRAM.
- Send one silent priming frame while opening the speaker lane, allow a bounded
  eight-second startup send, and report readiness only after it returns.
- Set `api.reboot_timeout: 0s`. This endpoint is Joydex-owned and intentionally
  has no Home Assistant API client; the Wi-Fi reboot timeout still covers
  network loss.

The transport remains plaintext. Later Sendspin encryption changes both the
handshake and audio-frame layout and requires a separate decision.

## Rejected candidate and corrections

The first physical adapter build reached decoded speaker writes and then
rebooted. USB serial identified `A stack overflow in task pthread has been
detected`. The upstream thread helper requested a PSRAM capability combination
rejected by the pinned ESP-IDF version, ignored that configuration failure, and
left the 3,072-byte default stack active. Assigning the 6,192-byte player stack
to internal RAM fixed the panic; two 40-second full-duplex controls then passed.

A later natural call stayed booted but delivered its first real speaker frame
while the stream was still synchronizing. Synchronization took about 2.86
seconds, longer than the former three-second per-send recovery deadline. The
silent priming frame and explicit eight-second startup bound corrected that
race without retrying a partial stream.

An unrelated red ring at 15 minutes was the stock native-API no-client reboot,
not a media failure. Disabling that unused watchdog kept the Joydex-owned
endpoint alive while retaining network-loss recovery.

## Consequences

- This is a narrow player adapter, not a voice-stack redesign. Wake handling,
  Codex task ownership, microphone uplink, cues, session controls, and host
  scheduling remain unchanged.
- The pinned ABI boundary and backports are verified by hashes during source
  preparation. The supported build also verifies the exact upstream commits and
  copied component set.
- Host compatibility checks cannot substitute for physical I2S and long-call
  tests. Device installation and rollback remain attended operations.
- Short replies that begin near a timestamp rebase remain a targeted acoustic
  risk; preserve onset and played-frame telemetry when changing scheduling.

## Evidence retained

- `tools/Joydex.SendspinModernCanary` and
  `tools/Joydex.SendspinProductionCanary` negotiated the exact production
  player-v1 contract against the pinned modern client. Three two-second
  responses shared one call-scoped stream and all exceeded 90,000 audible
  decoded frames with zero write timeout or format mismatch.
- The source-only ESPHome build passed twice with reproducible-build metadata,
  exact component hashes, ABI assertions, and image validation.
- After the stack, startup, and API-watchdog corrections, an approximately
  321-second natural session completed 24 assistant playback boundaries, 8,173
  assistant frames, and 15,982 Sendspin frames with host queue high-water two,
  zero overflow, and maximum pacing lateness 11.8 ms. Spoken hangup ended the
  session and the endpoint rearmed without rebooting.
